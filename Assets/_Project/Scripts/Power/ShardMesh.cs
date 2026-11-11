using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// Procedural hard-edged shard meshes for the fractured halo: a long, slightly twisted,
    /// irregular bipyramid (flat-shaded, no shared vertices, so every facet catches light).
    /// </summary>
    public static class ShardMesh
    {
        static Mesh[] s_Meshes;

        public static Mesh Get(int index)
        {
            if (s_Meshes == null)
            {
                s_Meshes = new Mesh[5];
                for (var i = 0; i < s_Meshes.Length; i++) s_Meshes[i] = Build(new System.Random(97 + i * 31));
            }
            return s_Meshes[Mathf.Abs(index) % s_Meshes.Length];
        }

        static Mesh Build(System.Random rng)
        {
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            // ring of 3-5 points around the middle, two tips along +Z / -Z (unit length along Z)
            var sides = 3 + rng.Next(3);
            var ring = new Vector3[sides];
            for (var i = 0; i < sides; i++)
            {
                var a = i * Mathf.PI * 2f / sides + R(-0.35f, 0.35f);
                var r = R(0.07f, 0.16f);
                ring[i] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * R(0.5f, 1f), R(-0.1f, 0.15f));
            }
            var tipA = new Vector3(R(-0.03f, 0.03f), R(-0.03f, 0.03f), 0.5f + R(0f, 0.15f));
            var tipB = new Vector3(R(-0.03f, 0.03f), R(-0.03f, 0.03f), -0.3f - R(0f, 0.1f));

            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                var n = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c);
                tris.Add(n); tris.Add(n + 1); tris.Add(n + 2);
            }
            for (var i = 0; i < sides; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % sides];
                Tri(a, b, tipA);
                Tri(b, a, tipB);
            }
            var mesh = new Mesh { name = "HaloShard" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
