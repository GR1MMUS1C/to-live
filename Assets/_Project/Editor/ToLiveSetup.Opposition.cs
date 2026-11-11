using ToLive.Opposition;
using ToLive.Rival;
using Unity.NetCode;
using Unity.Rendering;
using UnityEditor;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Milestone 3B generated content: grey-box response unit prefabs (police drone, gunship,
    /// anti-god weapon), the rocket, the replicated opposition state, and the Opposition / Rival
    /// config objects in the District subscene. Silhouettes over looks: each type reads at a glance.
    /// </summary>
    public static partial class ToLiveSetup
    {
        const string PolicePrefabPath = PrefabsDir + "/PoliceDrone.prefab";
        const string GunshipPrefabPath = PrefabsDir + "/Gunship.prefab";
        const string AntiGodPrefabPath = PrefabsDir + "/AntiGodWeapon.prefab";
        const string RocketPrefabPath = PrefabsDir + "/Rocket.prefab";
        const string OppositionStatePrefabPath = PrefabsDir + "/OppositionState.prefab";

        static Material s_UnitMat, s_PoliceLightMat, s_ThreatMat, s_RocketMat;

        static void CreateOppositionPrefabs()
        {
            if (s_BodyMat == null) CreateMaterials();
            s_UnitMat = GetOrCreateMaterial("M_UnitBody", Color.white, 0.35f);
            s_PoliceLightMat = GetOrCreateMaterial("M_PoliceLight", new Color(0.35f, 0.5f, 0.9f), 0.6f);
            s_ThreatMat = GetOrCreateMaterial("M_ThreatRed", new Color(0.9f, 0.12f, 0.08f), 0.6f);
            s_RocketMat = GetOrCreateMaterial("M_Rocket", new Color(0.8f, 0.78f, 0.7f), 0.4f);

            // ---- Police drone: a flat quad-rotor slab with a blue light bar ----
            {
                var root = UnitRoot("PoliceDrone", UnitKind.Police, out var visual);
                var body = Part(visual, PrimitiveType.Cube, "Body", new Vector3(0, 0, 0), new Vector3(1.6f, 0.45f, 1.6f), s_UnitMat, true);
                foreach (var c in new[] { new Vector3(1f, 0.15f, 1f), new Vector3(-1f, 0.15f, 1f), new Vector3(1f, 0.15f, -1f), new Vector3(-1f, 0.15f, -1f) })
                    Part(visual, PrimitiveType.Cylinder, "Rotor", c, new Vector3(0.9f, 0.03f, 0.9f), s_UnitMat, false);
                Part(visual, PrimitiveType.Cube, "LightBar", new Vector3(0, 0.3f, 0.2f), new Vector3(1.2f, 0.14f, 0.25f), s_PoliceLightMat, false);
                Part(visual, PrimitiveType.Cube, "Front", new Vector3(0, 0, 0.9f), new Vector3(0.5f, 0.2f, 0.3f), s_NoseMat, false);
                Finish(root, body, null, PolicePrefabPath);
            }

            // ---- Gunship: fuselage, tail boom, stub wings, rocket pods that aim ----
            {
                var root = UnitRoot("Gunship", UnitKind.Gunship, out var visual);
                var body = Part(visual, PrimitiveType.Cube, "Body", new Vector3(0, 0, 0.5f), new Vector3(2.6f, 1.8f, 5f), s_UnitMat, true);
                Part(visual, PrimitiveType.Cube, "Tail", new Vector3(0, 0.3f, -3.8f), new Vector3(0.6f, 0.6f, 4f), s_UnitMat, false);
                Part(visual, PrimitiveType.Cube, "Fin", new Vector3(0, 1.1f, -5.4f), new Vector3(0.15f, 1.6f, 0.9f), s_UnitMat, false);
                Part(visual, PrimitiveType.Cube, "Wings", new Vector3(0, -0.2f, 0.4f), new Vector3(6.4f, 0.22f, 1.3f), s_UnitMat, false);
                Part(visual, PrimitiveType.Cylinder, "Rotor", new Vector3(0, 1.1f, 0.5f), new Vector3(9f, 0.03f, 9f), s_UnitMat, false);
                Part(visual, PrimitiveType.Cube, "Canopy", new Vector3(0, 0.3f, 3.1f), new Vector3(1.6f, 0.9f, 0.6f), s_NoseMat, false);
                var pods = new GameObject("Pods");
                pods.transform.SetParent(visual.transform, false);
                pods.transform.localPosition = new Vector3(0, -0.6f, 0.6f);
                foreach (var x in new[] { -2.4f, 2.4f })
                {
                    var pod = Part(pods, PrimitiveType.Cylinder, "Pod", new Vector3(x, 0, 0.6f), new Vector3(0.55f, 1.1f, 0.55f), s_UnitMat, false);
                    pod.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    var tip = Part(pods, PrimitiveType.Sphere, "PodTip", new Vector3(x, 0, 1.75f), new Vector3(0.45f, 0.45f, 0.45f), s_ThreatMat, false);
                }
                Finish(root, body, pods, GunshipPrefabPath);
            }

            // ---- Anti-god weapon: a big dark core with a ring and a long barrel with a red emitter ----
            {
                var root = UnitRoot("AntiGodWeapon", UnitKind.AntiGod, out var visual);
                var body = Part(visual, PrimitiveType.Sphere, "Body", Vector3.zero, new Vector3(7f, 6f, 7f), s_UnitMat, true);
                Part(visual, PrimitiveType.Cylinder, "Ring", Vector3.zero, new Vector3(11f, 0.25f, 11f), s_UnitMat, false);
                foreach (var a in new[] { 0f, 120f, 240f })
                {
                    var fin = Part(visual, PrimitiveType.Cube, "Fin", Quaternion.Euler(0, a, 0) * new Vector3(0, 0, 5.2f), new Vector3(0.4f, 3.5f, 2.2f), s_UnitMat, false);
                    fin.transform.localRotation = Quaternion.Euler(0, a, 0);
                }
                var barrel = new GameObject("Barrel");
                barrel.transform.SetParent(visual.transform, false);
                var tube = Part(barrel, PrimitiveType.Cylinder, "Tube", new Vector3(0, 0, 5f), new Vector3(1.3f, 4f, 1.3f), s_UnitMat, false);
                tube.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                Part(barrel, PrimitiveType.Sphere, "Emitter", new Vector3(0, 0, 9.2f), new Vector3(1.6f, 1.6f, 1.6f), s_ThreatMat, false);
                Finish(root, body, barrel, AntiGodPrefabPath);
            }

            // ---- Rocket: small, slow, visible ----
            {
                var root = new GameObject("Rocket");
                var ghost = root.AddComponent<GhostAuthoringComponent>();
                ghost.DefaultGhostMode = GhostMode.Interpolated;
                ghost.SupportedGhostModes = GhostModeMask.Interpolated;
                ghost.HasOwner = false;
                ghost.Importance = 2; // visible threats update first
                var tube = Part(root, PrimitiveType.Cylinder, "Body", Vector3.zero, new Vector3(0.35f, 0.7f, 0.35f), s_RocketMat, false);
                tube.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                Part(root, PrimitiveType.Sphere, "Tip", new Vector3(0, 0, 0.75f), new Vector3(0.38f, 0.38f, 0.5f), s_ThreatMat, false);
                Part(root, PrimitiveType.Sphere, "Exhaust", new Vector3(0, 0, -0.8f), new Vector3(0.45f, 0.45f, 0.45f), s_NoseMat, false);
                root.AddComponent<RocketAuthoring>();
                PrefabUtility.SaveAsPrefabAsset(root, RocketPrefabPath);
                Object.DestroyImmediate(root);
            }

            // ---- Replicated heat / rival state ----
            {
                var state = new GameObject("OppositionState");
                var g = state.AddComponent<GhostAuthoringComponent>();
                g.DefaultGhostMode = GhostMode.Interpolated;
                g.SupportedGhostModes = GhostModeMask.Interpolated;
                g.HasOwner = false;
                state.AddComponent<OppositionStateAuthoring>();
                PrefabUtility.SaveAsPrefabAsset(state, OppositionStatePrefabPath);
                Object.DestroyImmediate(state);
            }
        }

        static GameObject UnitRoot(string name, byte kind, out GameObject visual)
        {
            var root = new GameObject(name);
            var ghost = root.AddComponent<GhostAuthoringComponent>();
            ghost.DefaultGhostMode = GhostMode.Interpolated; // server-simulated, interpolated everywhere (like the dummy)
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.HasOwner = false;
            visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            root.AddComponent<ResponseUnitAuthoring>().Kind = kind;
            return root;
        }

        static GameObject Part(GameObject parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat, bool tinted)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>()); // units collide by sphere sweeps, not colliders
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (tinted) go.AddComponent<URPMaterialPropertyBaseColorAuthoring>().color = Color.white;
            return go;
        }

        static void Finish(GameObject root, GameObject body, GameObject aim, string path)
        {
            var a = root.GetComponent<ResponseUnitAuthoring>();
            a.Body = body;
            a.Aim = aim;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        /// <summary>Opposition Config (heat + response force) and Rival Config in the District subscene.</summary>
        static void AddOppositionObjects()
        {
            var opp = new GameObject("Opposition Config").AddComponent<OppositionConfigAuthoring>();
            opp.PolicePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PolicePrefabPath);
            opp.GunshipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunshipPrefabPath);
            opp.AntiGodPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AntiGodPrefabPath);
            opp.RocketPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RocketPrefabPath);
            opp.StatePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(OppositionStatePrefabPath);
            new GameObject("Rival Config").AddComponent<RivalConfigAuthoring>();
        }
    }
}
