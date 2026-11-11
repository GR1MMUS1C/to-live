using System.Collections.Generic;
using ToLive.District;
using Unity.NetCode;
using Unity.Rendering;
using UnityEditor;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Milestone 4A generated content: the district ghost, citizen body ghost, the client-only citizen
    /// render prefab, the benchmark citizen-ghost probe, the citizens' homes along the sidewalks,
    /// roof crowns and street lamps that take the district's colour, and the District Config object.
    /// </summary>
    public static partial class ToLiveSetup
    {
        const string DistrictStatePrefabPath = PrefabsDir + "/DistrictState.prefab";
        const string CitizenBodyPrefabPath = PrefabsDir + "/CitizenBody.prefab";
        const string CitizenPrefabPath = PrefabsDir + "/Citizen.prefab";
        const string CitizenProbePrefabPath = PrefabsDir + "/CitizenGhostProbe.prefab";
        const int MaxHomes = 640;

        static Material s_CitizenMat, s_BloodMat, s_AccentMat, s_LampPoleMat;

        static void CreateDistrictPrefabs()
        {
            if (s_BodyMat == null) CreateMaterials();
            s_CitizenMat = GetOrCreateMaterial("M_Citizen", Color.white, 0.25f);
            s_LampPoleMat = GetOrCreateMaterial("M_LampPole", new Color(0.3f, 0.3f, 0.32f), 0.3f);
            s_BloodMat = GetOrCreateShaderMaterial("M_Blood", "Universal Render Pipeline/Unlit");
            s_BloodMat.SetColor("_BaseColor", new Color(0.5f, 0.03f, 0.03f)); // unlit: stays readable in building shadow
            EditorUtility.SetDirty(s_BloodMat);
            s_AccentMat = GetOrCreateShaderMaterial("M_DistrictAccent", "Universal Render Pipeline/Unlit");
            s_AccentMat.SetColor("_BaseColor", Color.white); // tinted per instance at runtime
            s_AccentMat.enableInstancing = true;
            EditorUtility.SetDirty(s_AccentMat);

            // district state ghost (dynamic: alive/mood bits, standings, threats, feed)
            {
                var go = new GameObject("DistrictState");
                var g = go.AddComponent<GhostAuthoringComponent>();
                g.DefaultGhostMode = GhostMode.Interpolated;
                g.SupportedGhostModes = GhostModeMask.Interpolated;
                g.HasOwner = false;
                go.AddComponent<DistrictStateAuthoring>();
                PrefabUtility.SaveAsPrefabAsset(go, DistrictStatePrefabPath);
                Object.DestroyImmediate(go);
            }

            // body: static-optimised ghost (sent once), lying figure + blood pool, no dismemberment
            {
                var go = new GameObject("CitizenBody");
                var g = go.AddComponent<GhostAuthoringComponent>();
                g.DefaultGhostMode = GhostMode.Interpolated;
                g.SupportedGhostModes = GhostModeMask.Interpolated;
                g.OptimizationMode = GhostOptimizationMode.Static;
                g.HasOwner = false;
                g.Importance = 1;
                var figure = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                figure.name = "Figure";
                Object.DestroyImmediate(figure.GetComponent<Collider>());
                figure.transform.SetParent(go.transform, false);
                figure.transform.localPosition = new Vector3(0f, 0.22f, 0f);
                figure.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                figure.transform.localScale = new Vector3(0.45f, 0.85f, 0.4f);
                figure.GetComponent<MeshRenderer>().sharedMaterial = s_CitizenMat;
                figure.AddComponent<URPMaterialPropertyBaseColorAuthoring>().color = new Color(0.62f, 0.6f, 0.58f);
                var blood = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                blood.name = "Blood";
                Object.DestroyImmediate(blood.GetComponent<Collider>());
                blood.transform.SetParent(go.transform, false);
                blood.transform.localPosition = new Vector3(0.15f, 0.02f, 0.1f);
                blood.transform.localScale = new Vector3(2.6f, 0.005f, 2.1f);
                blood.GetComponent<MeshRenderer>().sharedMaterial = s_BloodMat;
                blood.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.AddComponent<CitizenBodyAuthoring>();
                PrefabUtility.SaveAsPrefabAsset(go, CitizenBodyPrefabPath);
                Object.DestroyImmediate(go);
            }

            // citizen render prefab (client-only, not a ghost): a small grey capsule
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Citizen";
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.localScale = new Vector3(0.45f, 0.9f, 0.45f);
                go.GetComponent<MeshRenderer>().sharedMaterial = s_CitizenMat;
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.AddComponent<URPMaterialPropertyBaseColorAuthoring>().color = new Color(0.5f, 0.5f, 0.5f);
                PrefabUtility.SaveAsPrefabAsset(go, CitizenPrefabPath);
                Object.DestroyImmediate(go);
            }

            // benchmark option A: one ghost per citizen
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "CitizenGhostProbe";
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.localScale = new Vector3(0.45f, 0.9f, 0.45f);
                go.GetComponent<MeshRenderer>().sharedMaterial = s_CitizenMat;
                var g = go.AddComponent<GhostAuthoringComponent>();
                g.DefaultGhostMode = GhostMode.Interpolated;
                g.SupportedGhostModes = GhostModeMask.Interpolated;
                g.HasOwner = false;
                go.AddComponent<CitizenProbeAuthoring>();
                PrefabUtility.SaveAsPrefabAsset(go, CitizenProbePrefabPath);
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Homes on the sidewalks, roof crowns, street lamps and District Config (called by the district generator).</summary>
        static void AddDistrictObjects(List<GameObject> buildings, GameObject ground)
        {
            Physics.SyncTransforms();
            var blockSize = (DistrictSize - StreetWidth * (BlocksPerSide + 1)) / BlocksPerSide;
            var centres = new List<float>();
            for (var k = 0; k <= BlocksPerSide; k++) centres.Add(-DistrictSize / 2f + StreetWidth / 2f + k * (blockSize + StreetWidth));

            // ---- homes: both sidewalks of every street, away from intersections, checked by raycast ----
            var homes = new List<Vector3>(); var streets = new List<Vector3>();
            var side = StreetWidth / 2f - 2.5f;
            foreach (var c in centres)
            for (var axis = 0; axis < 2; axis++)
            for (var t = -DistrictSize / 2f + 6f; t <= DistrictSize / 2f - 6f; t += 7f)
            {
                var nearCross = false;
                foreach (var c2 in centres) if (Mathf.Abs(t - c2) < StreetWidth / 2f + 3f) nearCross = true;
                if (nearCross) continue;
                foreach (var s in new[] { -side, side })
                {
                    var p = axis == 0 ? new Vector3(c + s, 0f, t) : new Vector3(t, 0f, c + s);
                    if (!Physics.Raycast(p + Vector3.up * 400f, Vector3.down, out var hit, 500f) || hit.collider.gameObject != ground) continue;
                    homes.Add(new Vector3(p.x, 0f, p.z));
                    streets.Add(axis == 0 ? Vector3.forward : Vector3.right);
                }
            }
            // deterministic shuffle so the first N citizens are spread over the whole district
            var rng = new System.Random(Seed + 4);
            for (var i = homes.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (homes[i], homes[j]) = (homes[j], homes[i]);
                (streets[i], streets[j]) = (streets[j], streets[i]);
            }
            if (homes.Count > MaxHomes) { homes.RemoveRange(MaxHomes, homes.Count - MaxHomes); streets.RemoveRange(MaxHomes, streets.Count - MaxHomes); }
            var layout = new GameObject("Crowd Layout").AddComponent<CrowdLayoutAuthoring>();
            layout.Homes = homes; layout.Streets = streets;

            // ---- roof crowns: a glowing band around the top of every building ----
            var accents = new GameObject("District Accents");
            foreach (var b in buildings)
            {
                if (b == null) continue;
                var s = b.transform.localScale; var p = b.transform.position;
                var crown = GameObject.CreatePrimitive(PrimitiveType.Cube);
                crown.name = "Crown";
                Object.DestroyImmediate(crown.GetComponent<Collider>());
                crown.transform.SetParent(accents.transform, false);
                crown.transform.position = new Vector3(p.x, p.y + s.y / 2f - 2f, p.z);
                crown.transform.localScale = new Vector3(s.x + 0.6f, 3f, s.z + 0.6f);
                Accent(crown, 1f);
                crown.isStatic = true;
            }
            // ---- street lamps at every intersection ----
            foreach (var cx in centres)
            foreach (var cz in centres)
            {
                var pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pole.name = "LampPole";
                Object.DestroyImmediate(pole.GetComponent<Collider>());
                pole.transform.SetParent(accents.transform, false);
                pole.transform.position = new Vector3(cx + 7f, 4f, cz + 7f);
                pole.transform.localScale = new Vector3(0.3f, 8f, 0.3f);
                pole.GetComponent<MeshRenderer>().sharedMaterial = s_LampPoleMat;
                pole.isStatic = true;
                var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
                head.name = "LampHead";
                Object.DestroyImmediate(head.GetComponent<Collider>());
                head.transform.SetParent(accents.transform, false);
                head.transform.position = new Vector3(cx + 7f, 8.2f, cz + 7f);
                head.transform.localScale = new Vector3(2.2f, 0.4f, 2.2f);
                Accent(head, 1.5f);
                head.isStatic = true;
            }

            // ---- milestone 4B: fortification spots (towers raised when the district is dominated) ----
            var forts = new GameObject("Fortifications");
            var fortSpots = new List<Vector2>();
            for (var a = 1; a < centres.Count - 1; a++)
            for (var b2 = 1; b2 < centres.Count - 1; b2++)
                if ((a + b2) % 2 == 0) fortSpots.Add(new Vector2(centres[a] - 7f, centres[b2] - 7f));
            for (var i = 0; i < fortSpots.Count && i < 8; i++)
            {
                var sx = fortSpots[i].x; var sz = fortSpots[i].y;
                var shaft = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shaft.name = $"Tower_{i}";
                Object.DestroyImmediate(shaft.GetComponent<Collider>());
                shaft.transform.SetParent(forts.transform, false);
                shaft.transform.position = new Vector3(sx, 8f, sz);
                shaft.transform.localScale = new Vector3(3f, 16f, 3f);
                shaft.GetComponent<MeshRenderer>().sharedMaterial = s_LampPoleMat;
                shaft.AddComponent<ToLive.Crisis.FortificationAuthoring>().Index = i;
                shaft.GetComponent<ToLive.Crisis.FortificationAuthoring>().TopHeight = 8.5f;
                var band = GameObject.CreatePrimitive(PrimitiveType.Cube);
                band.name = $"TowerBand_{i}";
                Object.DestroyImmediate(band.GetComponent<Collider>());
                band.transform.SetParent(forts.transform, false);
                band.transform.position = new Vector3(sx, 15.5f, sz);
                band.transform.localScale = new Vector3(3.6f, 1.4f, 3.6f);
                Accent(band, 1.6f);
                band.AddComponent<ToLive.Crisis.FortificationAuthoring>().Index = i;
                band.GetComponent<ToLive.Crisis.FortificationAuthoring>().TopHeight = 1f;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = $"Barricade_{i}";
                Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.transform.SetParent(forts.transform, false);
                wall.transform.position = new Vector3(sx + 5f, 1f, sz);
                wall.transform.localScale = new Vector3(1f, 2f, 9f);
                wall.GetComponent<MeshRenderer>().sharedMaterial = s_LampPoleMat;
                wall.AddComponent<ToLive.Crisis.FortificationAuthoring>().Index = i;
                wall.GetComponent<ToLive.Crisis.FortificationAuthoring>().TopHeight = 15.5f;
            }
            new GameObject("Crisis Config").AddComponent<ToLive.Crisis.CrisisConfigAuthoring>();

            var cfg = new GameObject("District Config").AddComponent<DistrictConfigAuthoring>();
            cfg.StatePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DistrictStatePrefabPath);
            cfg.BodyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CitizenBodyPrefabPath);
            cfg.CitizenPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CitizenPrefabPath);
            cfg.ProbePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CitizenProbePrefabPath);
            Debug.Log($"[ToLive] District: {homes.Count} citizen homes, {buildings.Count} roof crowns, {centres.Count * centres.Count} lamps");
        }

        static void Accent(GameObject go, float strength)
        {
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = s_AccentMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<URPMaterialPropertyBaseColorAuthoring>().color = new Color(0.16f, 0.16f, 0.17f);
            go.AddComponent<DistrictAccentAuthoring>().Strength = strength;
        }
    }
}
