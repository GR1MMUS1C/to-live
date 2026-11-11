using System.Collections.Generic;
using ToLive.Destruction;
using UnityEditor;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Our own layered pre-fracture for grey-box buildings (done at authoring time, like Unreal's
    /// Geometry Collection fracture but much simpler):
    ///  - LAYER 1: structural chunks. The building is cut into storey "bands" of random height.
    ///    Each band gets outer wall panels (cut at jittered positions, sometimes split again
    ///    vertically, so it doesn't read as a brick grid), a floor slab ring and a central core.
    ///    So holes show floors and a core inside instead of an empty shell.
    ///  - LAYER 2: each chunk is recursively split into 4-8 smaller boxes that become cosmetic
    ///    debris when the chunk breaks.
    /// It also records which chunks touch each other and which stand on the ground, for the
    /// runtime structural-support check.
    /// </summary>
    public static class BreakableBuildingGenerator
    {
        public struct Box
        {
            public Vector3 Min, Max;
            public bool Interior;
            public Vector3 Center => (Min + Max) * 0.5f;
            public Vector3 Size => Max - Min;
            public float Volume => Size.x * Size.y * Size.z;
        }

        const float WallThickness = 1.2f;
        const float SlabThickness = 0.6f;

        [MenuItem("Tools/To Live/Make Selected Building Breakable", priority = 20)]
        static void MakeSelectedBreakable()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<BoxCollider>() == null)
            {
                EditorUtility.DisplayDialog("To Live", "Select one box building (with a BoxCollider) in the open District scene first.", "OK");
                return;
            }
            var b = go.GetComponent<Renderer>().bounds;
            var mats = new[] { go.GetComponent<Renderer>().sharedMaterial };
            var root = Generate(b.center, b.size, go.transform.parent, mats, mats[0], 1234);
            Undo.RegisterCreatedObjectUndo(root.gameObject, "Make breakable");
            Undo.DestroyObjectImmediate(go);
            Selection.activeGameObject = root.gameObject;
        }

        /// <summary>Builds the chunk hierarchy for a box building centred at <paramref name="center"/>.</summary>
        public static BreakableBuildingAuthoring Generate(Vector3 center, Vector3 size, Transform parent,
            Material[] wallMaterials, Material interiorMaterial, int seed)
        {
            List<Box> chunks = null;
            // Retry with fewer cuts until we fit the 128-chunk replicated mask (aim for 60-120).
            for (var attempt = 0; attempt < 6; attempt++)
            {
                chunks = Fracture(center, size, new System.Random(seed + attempt), 1f - attempt * 0.12f);
                if (chunks.Count <= 120) break;
            }
            if (chunks.Count > BuildingState.MaxChunks) chunks.RemoveRange(BuildingState.MaxChunks, chunks.Count - BuildingState.MaxChunks);

            var root = new GameObject("Breakable Building");
            root.transform.SetParent(parent, false);
            var authoring = root.AddComponent<BreakableBuildingAuthoring>();
            var rng = new System.Random(seed * 7 + 3);

            for (var i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube); // keeps its BoxCollider -> static collider
                go.name = $"Chunk_{i:000}";
                go.transform.SetParent(root.transform, false);
                go.transform.position = c.Center;
                go.transform.localScale = c.Size;
                go.isStatic = true;
                go.GetComponent<MeshRenderer>().sharedMaterial = c.Interior ? interiorMaterial : wallMaterials[rng.Next(wallMaterials.Length)];

                var ca = go.AddComponent<ChunkAuthoring>();
                ca.Index = i;
                foreach (var p in SplitPieces(c, rng))
                {
                    ca.PieceCenters.Add(p.Center - c.Center);
                    ca.PieceSizes.Add(p.Size);
                }

                authoring.Chunks.Add(new BreakableBuildingAuthoring.ChunkData
                {
                    Center = c.Center, Size = c.Size,
                    Grounded = c.Min.y <= 0.05f,
                    Neighbors = FindNeighbors(chunks, i),
                });
            }
            return authoring;
        }

        static List<Box> Fracture(Vector3 center, Vector3 size, System.Random rng, float detail)
        {
            var list = new List<Box>();
            var min = new Vector3(center.x - size.x / 2f, 0f, center.z - size.z / 2f);
            var max = new Vector3(center.x + size.x / 2f, size.y, center.z + size.z / 2f);
            var h = size.y;
            var t = WallThickness;

            // Core footprint (central lift/stair core).
            var coreHalf = new Vector2(size.x * 0.14f, size.z * 0.14f);
            var inner = new Vector2(size.x / 2f - t, size.z / 2f - t);

            var y = 0f;
            var bands = new List<(float y0, float y1)>();
            while (y < h - 0.01f)
            {
                var bandH = Mathf.Lerp(9f, 14f, (float)rng.NextDouble());
                if (h - (y + bandH) < 6f) bandH = h - y;
                bands.Add((y, y + bandH));
                y += bandH;
            }

            foreach (var (y0, y1) in bands)
            {
                // Outer walls: 4 faces, each cut into 2-4 panels at jittered positions.
                AddWall(list, rng, detail, true, min.x, max.x, max.z - t, max.z, y0, y1);   // +Z (full width)
                AddWall(list, rng, detail, true, min.x, max.x, min.z, min.z + t, y0, y1);   // -Z
                AddWall(list, rng, detail, false, max.x - t, max.x, min.z + t, max.z - t, y0, y1); // +X (between)
                AddWall(list, rng, detail, false, min.x, min.x + t, min.z + t, max.z - t, y0, y1); // -X

                // Floor slab ring at the bottom of the band (not on the ground floor).
                if (y0 > 0.01f) AddSlabRing(list, center, inner, coreHalf, y0, y0 + SlabThickness);

                // Core column.
                list.Add(new Box
                {
                    Min = new Vector3(center.x - coreHalf.x, y0, center.z - coreHalf.y),
                    Max = new Vector3(center.x + coreHalf.x, y1, center.z + coreHalf.y),
                    Interior = true,
                });
            }
            // Roof.
            AddSlabRing(list, center, inner, coreHalf, h - SlabThickness, h);
            return list;
        }

        static void AddWall(List<Box> list, System.Random rng, float detail, bool alongX,
            float x0, float x1, float z0, float z1, float y0, float y1)
        {
            var length = alongX ? x1 - x0 : z1 - z0;
            var segments = Mathf.Clamp(2 + (int)(rng.NextDouble() * 3 * detail), 2, 4);
            var cuts = new List<float> { 0f };
            for (var s = 1; s < segments; s++)
            {
                var even = s / (float)segments;
                cuts.Add(Mathf.Clamp(even + ((float)rng.NextDouble() - 0.5f) * 0.5f / segments, 0.1f, 0.9f));
            }
            cuts.Add(1f);
            cuts.Sort();

            for (var s = 0; s < cuts.Count - 1; s++)
            {
                var a = cuts[s] * length;
                var b = cuts[s + 1] * length;
                if (b - a < 1.5f) continue;
                Box seg = alongX
                    ? new Box { Min = new Vector3(x0 + a, y0, z0), Max = new Vector3(x0 + b, y1, z1) }
                    : new Box { Min = new Vector3(x0, y0, z0 + a), Max = new Vector3(x1, y1, z0 + b) };

                if (rng.NextDouble() < 0.35 * detail && y1 - y0 > 6f)
                {
                    var cut = Mathf.Lerp(y0, y1, Mathf.Lerp(0.35f, 0.65f, (float)rng.NextDouble()));
                    var lower = seg; lower.Max.y = cut;
                    var upper = seg; upper.Min.y = cut;
                    list.Add(lower);
                    list.Add(upper);
                }
                else list.Add(seg);
            }
        }

        static void AddSlabRing(List<Box> list, Vector3 c, Vector2 inner, Vector2 core, float y0, float y1)
        {
            // north / south strips across the full inner width, east / west strips beside the core
            list.Add(new Box { Min = new Vector3(c.x - inner.x, y0, c.z + core.y), Max = new Vector3(c.x + inner.x, y1, c.z + inner.y), Interior = true });
            list.Add(new Box { Min = new Vector3(c.x - inner.x, y0, c.z - inner.y), Max = new Vector3(c.x + inner.x, y1, c.z - core.y), Interior = true });
            list.Add(new Box { Min = new Vector3(c.x + core.x, y0, c.z - core.y), Max = new Vector3(c.x + inner.x, y1, c.z + core.y), Interior = true });
            list.Add(new Box { Min = new Vector3(c.x - inner.x, y0, c.z - core.y), Max = new Vector3(c.x - core.x, y1, c.z + core.y), Interior = true });
        }

        /// <summary>Layer 2: split a chunk into 4-8 boxes by repeatedly cutting the biggest piece.</summary>
        static List<Box> SplitPieces(Box chunk, System.Random rng)
        {
            var target = 4 + rng.Next(5);
            var pieces = new List<Box> { chunk };
            while (pieces.Count < target)
            {
                var bi = 0;
                for (var i = 1; i < pieces.Count; i++) if (pieces[i].Volume > pieces[bi].Volume) bi = i;
                var b = pieces[bi];
                var s = b.Size;
                var axis = s.x >= s.y && s.x >= s.z ? 0 : (s.y >= s.z ? 1 : 2);
                var f = Mathf.Lerp(0.35f, 0.65f, (float)rng.NextDouble());
                var cut = Mathf.Lerp(b.Min[axis], b.Max[axis], f);
                var a = b; a.Max[axis] = cut;
                var c = b; c.Min[axis] = cut;
                pieces[bi] = a;
                pieces.Add(c);
            }
            return pieces;
        }

        static int[] FindNeighbors(List<Box> chunks, int i)
        {
            const float eps = 0.06f;
            var result = new List<int>();
            var a = chunks[i];
            for (var j = 0; j < chunks.Count; j++)
            {
                if (j == i) continue;
                var b = chunks[j];
                if (a.Min.x - eps <= b.Max.x && b.Min.x - eps <= a.Max.x &&
                    a.Min.y - eps <= b.Max.y && b.Min.y - eps <= a.Max.y &&
                    a.Min.z - eps <= b.Max.z && b.Min.z - eps <= a.Max.z)
                    result.Add(j);
            }
            return result.ToArray();
        }
    }
}
