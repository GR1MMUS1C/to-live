using System.IO;
using ToLive.CameraRig;
using ToLive.Flight;
using ToLive.Input;
using ToLive.Player;
using ToLive.UI;
using Unity.Cinemachine;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Reproducible project setup. Everything in Milestone 1 that isn't code (materials, the player
    /// prefab, the grey-box district subscene, the main scene) is generated here, so it can be
    /// rebuilt at any time from the Tools > To Live menu.
    /// </summary>
    public static class ToLiveSetup
    {
        const string Root = "Assets/_Project";
        const string MaterialsDir = Root + "/Materials";
        const string PrefabsDir = Root + "/Prefabs";
        const string ScenesDir = Root + "/Scenes";
        const string UIDir = Root + "/UI";
        const string PlayerPrefabPath = PrefabsDir + "/Player.prefab";
        const string DistrictScenePath = ScenesDir + "/District.unity";
        const string MainScenePath = ScenesDir + "/Main.unity";
        const string InputActionsPath = Root + "/Input/ToLive.inputactions";

        // ---------- District layout ----------
        const float DistrictSize = 600f;
        const float OutskirtsSize = 6000f;
        const int BlocksPerSide = 5;
        const float StreetWidth = 20f;
        const float MinHeight = 20f, MaxHeight = 200f;
        const int Seed = 20261110;

        [MenuItem("Tools/To Live/Setup Project (rebuild everything)", priority = 0)]
        public static void SetupProject()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureFolders();
            CreateMaterials();
            CreatePlayerPrefab();
            GenerateDistrictScene();
            CreateMainScene();
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MainScenePath, true) };
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            Debug.Log("[ToLive] Project setup complete. Open Assets/_Project/Scenes/Main.unity and press Play.");
        }

        [MenuItem("Tools/To Live/Generate Grey-box District", priority = 1)]
        public static void GenerateDistrictMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureFolders();
            CreateMaterials();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null) CreatePlayerPrefab();
            GenerateDistrictScene();
            if (File.Exists(MainScenePath)) EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            Debug.Log("[ToLive] District regenerated.");
        }

        static void EnsureFolders()
        {
            foreach (var dir in new[] { MaterialsDir, PrefabsDir, ScenesDir, UIDir })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
        }

        // ---------- Materials ----------
        static Material GetOrCreateMaterial(string name, Color color, float smoothness = 0.2f)
        {
            var path = $"{MaterialsDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material[] s_BuildingMats;
        static Material s_GroundMat, s_OutskirtsMat, s_BodyMat, s_NoseMat;

        static void CreateMaterials()
        {
            s_GroundMat = GetOrCreateMaterial("M_Ground", new Color(0.32f, 0.33f, 0.34f), 0.1f);
            s_OutskirtsMat = GetOrCreateMaterial("M_Outskirts", new Color(0.36f, 0.40f, 0.33f), 0.05f);
            s_BodyMat = GetOrCreateMaterial("M_PlayerBody", Color.white, 0.4f);
            s_NoseMat = GetOrCreateMaterial("M_PlayerFront", new Color(1f, 0.92f, 0.2f), 0.5f);
            s_BuildingMats = new[]
            {
                GetOrCreateMaterial("M_Building_A", new Color(0.62f, 0.62f, 0.64f)),
                GetOrCreateMaterial("M_Building_B", new Color(0.55f, 0.56f, 0.60f)),
                GetOrCreateMaterial("M_Building_C", new Color(0.66f, 0.64f, 0.60f)),
                GetOrCreateMaterial("M_Building_D", new Color(0.50f, 0.52f, 0.53f)),
            };
        }

        // ---------- Player ghost prefab ----------
        static void CreatePlayerPrefab()
        {
            if (s_BodyMat == null) CreateMaterials();
            var root = new GameObject("Player");
            var ghost = root.AddComponent<GhostAuthoringComponent>();
            ghost.DefaultGhostMode = GhostMode.OwnerPredicted; // predicted for the owner, interpolated for others
            ghost.SupportedGhostModes = GhostModeMask.All;
            ghost.HasOwner = true;
            ghost.SupportAutoCommandTarget = true;            // PlayerInput is routed automatically

            // Cosmetic pivot: leans the body forward into a "flying" pose at speed (client only).
            var pivot = new GameObject("Visual");
            pivot.transform.SetParent(root.transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule); // 2 m tall, 0.5 m radius
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>()); // collision is done by sphere sweeps
            body.transform.SetParent(pivot.transform, false);
            body.GetComponent<MeshRenderer>().sharedMaterial = s_BodyMat;
            var tint = body.AddComponent<URPMaterialPropertyBaseColorAuthoring>();
            tint.color = Color.white;

            // Clear "front" marker: a bright wedge at chest height pointing forward (+Z).
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "FrontMarker";
            Object.DestroyImmediate(nose.GetComponent<Collider>());
            nose.transform.SetParent(pivot.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0.35f, 0.55f);
            nose.transform.localScale = new Vector3(0.3f, 0.2f, 0.6f);
            nose.GetComponent<MeshRenderer>().sharedMaterial = s_NoseMat;

            var player = root.AddComponent<PlayerAuthoring>();
            player.Body = body;
            player.VisualPivot = pivot;

            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
        }

        // ---------- Grey-box district subscene ----------
        public static void GenerateDistrictScene()
        {
            if (s_GroundMat == null) CreateMaterials();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube); // BoxCollider -> static Unity Physics collider when baked
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(DistrictSize, 1f, DistrictSize);
            ground.GetComponent<MeshRenderer>().sharedMaterial = s_GroundMat;
            ground.isStatic = true;

            // Flat outskirts around the district so you can't fly off the edge of the world at 400 m/s.
            var outskirts = GameObject.CreatePrimitive(PrimitiveType.Cube);
            outskirts.name = "Outskirts";
            outskirts.transform.position = new Vector3(0f, -0.55f, 0f);
            outskirts.transform.localScale = new Vector3(OutskirtsSize, 1f, OutskirtsSize);
            outskirts.GetComponent<MeshRenderer>().sharedMaterial = s_OutskirtsMat;
            outskirts.isStatic = true;

            var buildings = new GameObject("Buildings");
            var rng = new System.Random(Seed);
            var blockSize = (DistrictSize - StreetWidth * (BlocksPerSide + 1)) / BlocksPerSide;
            var count = 0;
            for (var bx = 0; bx < BlocksPerSide; bx++)
            for (var bz = 0; bz < BlocksPerSide; bz++)
            {
                var blockMinX = -DistrictSize / 2f + StreetWidth + bx * (blockSize + StreetWidth);
                var blockMinZ = -DistrictSize / 2f + StreetWidth + bz * (blockSize + StreetWidth);
                var lots = 1 + rng.Next(3); // 1x1, 2x2 or 3x3 lots per block
                var lotSize = blockSize / lots;
                for (var lx = 0; lx < lots; lx++)
                for (var lz = 0; lz < lots; lz++)
                {
                    var inset = 2f + (float)rng.NextDouble() * 3f;
                    var w = lotSize - inset * 2f;
                    var d = lotSize - inset * 2f;
                    var t = (float)rng.NextDouble();
                    var h = Mathf.Lerp(MinHeight, MaxHeight, t * t); // more low/medium buildings than towers
                    var cx = blockMinX + (lx + 0.5f) * lotSize;
                    var cz = blockMinZ + (lz + 0.5f) * lotSize;

                    var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    b.name = $"Building_{bx}_{bz}_{lx}{lz}";
                    b.transform.SetParent(buildings.transform, false);
                    b.transform.position = new Vector3(cx, h / 2f, cz);
                    b.transform.localScale = new Vector3(w, h, d);
                    b.GetComponent<MeshRenderer>().sharedMaterial = s_BuildingMats[rng.Next(s_BuildingMats.Length)];
                    b.isStatic = true;
                    count++;
                }
            }

            // Spawn points above the streets, higher than the tallest building.
            var spawner = new GameObject("Player Spawner");
            var spawnerAuthoring = spawner.AddComponent<PlayerSpawnerAuthoring>();
            spawnerAuthoring.PlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var streetOffset = blockSize / 2f + StreetWidth / 2f;
            var spawnPositions = new[]
            {
                new Vector3(-streetOffset, MaxHeight + 30f, -streetOffset),
                new Vector3(streetOffset, MaxHeight + 30f, streetOffset),
                new Vector3(-streetOffset, MaxHeight + 30f, streetOffset),
                new Vector3(streetOffset, MaxHeight + 30f, -streetOffset),
            };
            for (var i = 0; i < spawnPositions.Length; i++)
            {
                var p = new GameObject($"SpawnPoint_{i + 1}");
                p.transform.SetParent(spawner.transform, false);
                p.transform.position = spawnPositions[i];
            }

            var flightConfig = new GameObject("Flight Config");
            flightConfig.AddComponent<FlightConfigAuthoring>();

            // Netcode only steps Unity Physics when it has a reason to. Our heroes are kinematic
            // (no rigidbody), so tell it to build the physics world whenever colliders exist; that's
            // what the flight sweeps and the camera collision query.
            var physicsConfig = new GameObject("NetCode Physics Config");
            var npc = physicsConfig.AddComponent<NetCodePhysicsConfig>();
            npc.PhysicGroupRunMode = PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities;

            EditorSceneManager.SaveScene(scene, DistrictScenePath);
            Debug.Log($"[ToLive] Generated district with {count} buildings at {DistrictScenePath}");
        }

        // ---------- Main scene ----------
        static void CreateMainScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Lighting: make the default directional light a nice late-morning sun.
            foreach (var light in Object.FindObjectsByType<Light>())
            {
                if (light.type != LightType.Directional) continue;
                light.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                light.intensity = 1.3f;
                light.shadows = LightShadows.Soft;
            }

            var mainCamera = Camera.main;
            mainCamera.farClipPlane = 3000f;
            mainCamera.nearClipPlane = 0.2f;
            mainCamera.gameObject.AddComponent<CinemachineBrain>();

            var target = new GameObject("CameraTarget");
            var cmGo = new GameObject("CM Flight Camera");
            var cm = cmGo.AddComponent<CinemachineCamera>();
            cm.Follow = target.transform;
            var lens = cm.Lens;
            lens.FieldOfView = 60f;
            lens.NearClipPlane = 0.2f;
            lens.FarClipPlane = 3000f;
            cm.Lens = lens;
            var follow = cmGo.AddComponent<CinemachineThirdPersonFollow>();
            follow.ShoulderOffset = Vector3.zero;
            follow.VerticalArmLength = 0f;
            follow.CameraSide = 0.5f;
            follow.CameraDistance = 6f;
            follow.Damping = new Vector3(0.05f, 0.05f, 0.05f);
            var avoid = follow.AvoidObstacles;
            avoid.Enabled = false; // the city has no PhysX colliders; FlightCameraRig handles collision with Unity Physics
            follow.AvoidObstacles = avoid;

            var systems = new GameObject("Game Systems");
            var reader = systems.AddComponent<InputReader>();
            reader.Actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            var rig = systems.AddComponent<FlightCameraRig>();
            rig.Target = target.transform;
            rig.Camera = cm;

            // Gameplay subscene (loaded into the client/server worlds by NetSession, not auto-loaded).
            var subSceneGo = new GameObject("District (SubScene)");
            var subScene = subSceneGo.AddComponent<SubScene>();
            subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(DistrictScenePath);
            subScene.AutoLoadScene = false;

            var uiGo = new GameObject("UI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings = GetOrCreatePanelSettings();
            var ui = uiGo.AddComponent<GameUI>();
            ui.GameplayScene = subScene;

            EditorSceneManager.SaveScene(scene, MainScenePath);
        }

        static PanelSettings GetOrCreatePanelSettings()
        {
            var themePath = UIDir + "/ToLiveTheme.tss";
            if (!File.Exists(themePath))
            {
                File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(themePath);
            }
            var path = UIDir + "/ToLivePanelSettings.asset";
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, path);
            }
            ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.match = 0.5f;
            EditorUtility.SetDirty(ps);
            return ps;
        }
    }
}
