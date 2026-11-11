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
    public static partial class ToLiveSetup
    {
        const string Root = "Assets/_Project";
        const string MaterialsDir = Root + "/Materials";
        const string PrefabsDir = Root + "/Prefabs";
        const string ScenesDir = Root + "/Scenes";
        const string UIDir = Root + "/UI";
        const string RenderingDir = Root + "/Rendering";
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
            CreateCombatPrefabs();
            CreateOppositionPrefabs();
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
            CreateCombatPrefabs();
            CreateOppositionPrefabs();
            GenerateDistrictScene();
            if (File.Exists(MainScenePath)) EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            Debug.Log("[ToLive] District regenerated.");
        }

        static void EnsureFolders()
        {
            foreach (var dir in new[] { MaterialsDir, PrefabsDir, ScenesDir, UIDir, RenderingDir })
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
        static Material s_GroundMat, s_OutskirtsMat, s_BodyMat, s_NoseMat, s_InteriorMat, s_DebrisMat;

        static void CreateMaterials()
        {
            s_GroundMat = GetOrCreateMaterial("M_Ground", new Color(0.32f, 0.33f, 0.34f), 0.1f);
            s_OutskirtsMat = GetOrCreateMaterial("M_Outskirts", new Color(0.36f, 0.40f, 0.33f), 0.05f);
            s_BodyMat = GetOrCreateMaterial("M_PlayerBody", Color.white, 0.4f);
            s_NoseMat = GetOrCreateMaterial("M_PlayerFront", new Color(1f, 0.92f, 0.2f), 0.5f);
            s_InteriorMat = GetOrCreateMaterial("M_Interior", new Color(0.42f, 0.41f, 0.40f));
            s_DebrisMat = GetOrCreateMaterial("M_Debris", new Color(0.55f, 0.54f, 0.52f));
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
            ghost.TrackInterpolationDelay = true;             // lets the server rewind hit checks to what this player saw

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

        // ---------- Combat / destruction prefabs ----------
        const string DummyPrefabPath = PrefabsDir + "/TrainingDummy.prefab";
        const string BuildingStatePrefabPath = PrefabsDir + "/BuildingState.prefab";
        const string DebrisPrefabPath = PrefabsDir + "/Debris.prefab";

        static void CreateCombatPrefabs()
        {
            if (s_BodyMat == null) CreateMaterials();

            // Training dummy: server-simulated, interpolated on every client, no owner.
            var root = new GameObject("TrainingDummy");
            var ghost = root.AddComponent<GhostAuthoringComponent>();
            ghost.DefaultGhostMode = GhostMode.Interpolated;
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.HasOwner = false;
            var pivot = new GameObject("Visual");
            pivot.transform.SetParent(root.transform, false);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(pivot.transform, false);
            body.transform.localScale = new Vector3(1.2f, 1.1f, 1.2f);
            body.GetComponent<MeshRenderer>().sharedMaterial = s_BodyMat;
            body.AddComponent<URPMaterialPropertyBaseColorAuthoring>().color = new Color(0.85f, 0.18f, 0.15f);
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "FrontMarker";
            Object.DestroyImmediate(nose.GetComponent<Collider>());
            nose.transform.SetParent(pivot.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0.35f, 0.6f);
            nose.transform.localScale = new Vector3(0.3f, 0.2f, 0.6f);
            nose.GetComponent<MeshRenderer>().sharedMaterial = s_NoseMat;
            var dummy = root.AddComponent<ToLive.Combat.TrainingDummyAuthoring>();
            dummy.Body = body;
            dummy.VisualPivot = pivot;
            PrefabUtility.SaveAsPrefabAsset(root, DummyPrefabPath);
            Object.DestroyImmediate(root);

            // Replicated building state (instanced once in the District as a pre-spawned ghost).
            var state = new GameObject("BuildingState");
            var sg = state.AddComponent<GhostAuthoringComponent>();
            sg.DefaultGhostMode = GhostMode.Interpolated;
            sg.SupportedGhostModes = GhostModeMask.Interpolated;
            sg.OptimizationMode = GhostOptimizationMode.Static; // only sends when the mask changes
            sg.HasOwner = false;
            state.AddComponent<ToLive.Destruction.BuildingStateAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(state, BuildingStatePrefabPath);
            Object.DestroyImmediate(state);

            // Debris render prefab: a unit cube with no collider (physics is added at runtime).
            var debris = GameObject.CreatePrimitive(PrimitiveType.Cube);
            debris.name = "Debris";
            Object.DestroyImmediate(debris.GetComponent<Collider>());
            debris.GetComponent<MeshRenderer>().sharedMaterial = s_DebrisMat;
            debris.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            PrefabUtility.SaveAsPrefabAsset(debris, DebrisPrefabPath);
            Object.DestroyImmediate(debris);
        }

        /// <summary>Replaces one mid-size building near player spawn 1 with a pre-fractured breakable one.</summary>
        static ToLive.Destruction.BreakableBuildingAuthoring PlaceBreakableBuilding(System.Collections.Generic.List<GameObject> placed, Vector3 nearXZ)
        {
            GameObject pick = null;
            var best = float.MaxValue;
            foreach (var b in placed)
            {
                var s = b.transform.localScale;
                if (s.y < 30f || s.y > 110f || Mathf.Min(s.x, s.z) < 18f) continue;
                var d = Vector2.Distance(new Vector2(b.transform.position.x, b.transform.position.z), new Vector2(nearXZ.x, nearXZ.z));
                if (d < best) { best = d; pick = b; }
            }
            if (pick == null) pick = placed[0];

            var size = pick.transform.localScale;
            size.y = Mathf.Clamp(size.y, 40f, 70f);
            size.x = Mathf.Min(size.x, 30f);
            size.z = Mathf.Min(size.z, 30f);
            var center = new Vector3(pick.transform.position.x, size.y / 2f, pick.transform.position.z);
            var parent = pick.transform.parent;
            placed.Remove(pick);
            Object.DestroyImmediate(pick);
            var building = BreakableBuildingGenerator.Generate(center, size, parent, s_BuildingMats, s_InteriorMat, Seed);
            Debug.Log($"[ToLive] Breakable building at {center} size {size}: {building.Chunks.Count} chunks");
            return building;
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
            var placed = new System.Collections.Generic.List<GameObject>();
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
                    placed.Add(b);
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

            // One breakable building near spawn 1, and the training dummy hovering in front of it.
            var breakable = PlaceBreakableBuilding(placed, spawnPositions[0]);
            var bc = Vector3.zero;
            foreach (var c in breakable.Chunks) bc += c.Center;
            bc /= Mathf.Max(1, breakable.Chunks.Count);
            var toSpawn = new Vector3(spawnPositions[0].x - bc.x, 0f, spawnPositions[0].z - bc.z).normalized;
            var dummyHome = new GameObject("Dummy Home");
            dummyHome.transform.SetParent(spawner.transform, false);
            dummyHome.transform.position = new Vector3(bc.x, 0f, bc.z) + toSpawn * 30f + Vector3.up * 25f;
            dummyHome.transform.rotation = Quaternion.LookRotation(toSpawn);
            spawnerAuthoring.DummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DummyPrefabPath);
            spawnerAuthoring.DummyHome = dummyHome.transform;

            var stateInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(BuildingStatePrefabPath), scene);
            stateInstance.transform.position = new Vector3(bc.x, -5f, bc.z);

            var combatConfig = new GameObject("Combat Config");
            combatConfig.AddComponent<ToLive.Combat.CombatConfigAuthoring>();
            new GameObject("Power Config").AddComponent<ToLive.Power.PowerConfigAuthoring>();
            var debrisConfig = new GameObject("Debris Config");
            debrisConfig.AddComponent<ToLive.Destruction.DebrisConfigAuthoring>().DebrisPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DebrisPrefabPath);

            AddOppositionObjects();

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
            var camData = mainCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (camData == null) camData = mainCamera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            var powerVolume = CreateVolumes();

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
            uiGo.AddComponent<CombatHud>();
            // Material assets for runtime effects (keeps their shaders in player builds).
            var fx = new GameObject("FX Materials").AddComponent<ToLive.Combat.FxMaterialSource>();
            fx.Unlit = GetOrCreateShaderMaterial("M_FxUnlit", "Universal Render Pipeline/Unlit");
            fx.Lit = GetOrCreateShaderMaterial("M_FxLit", "Universal Render Pipeline/Lit");
            fx.Particles = GetOrCreateShaderMaterial("M_FxParticlesAdditive", "Universal Render Pipeline/Particles/Unlit");
            fx.Particles.SetFloat("_Surface", 1f);
            fx.Particles.SetFloat("_Blend", 2f); // additive
            fx.Particles.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            fx.Particles.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            fx.Particles.SetFloat("_ZWrite", 0f);
            fx.Particles.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            fx.Particles.renderQueue = 3000;
            EditorUtility.SetDirty(fx.Particles);

            var lookGo = new GameObject("Power Look");
            lookGo.AddComponent<ToLive.Power.PowerLook>();
            lookGo.AddComponent<ToLive.Power.PowerPresenter>().PowerVolume = powerVolume;
            systems.AddComponent<PerfBenchmark>();
            ui.GameplayScene = subScene;

            EditorSceneManager.SaveScene(scene, MainScenePath);
        }

        /// <summary>
        /// Post-processing. Art rule: the world is muted, power is the only saturated colour.
        ///  - "Base Look" (global, always on): ACES tonemapping, desaturated, gentle bloom.
        ///  - "Power Volume" (global, weight 0): colour push + strong bloom; PowerPresenter raises its
        ///    weight during a ritual/surge and tints it with the energy colour.
        /// </summary>
        static UnityEngine.Rendering.Volume CreateVolumes()
        {
            var baseProfile = GetOrCreateProfile(RenderingDir + "/BaseLook.asset");
            var tone = Ensure<UnityEngine.Rendering.Universal.Tonemapping>(baseProfile);
            tone.mode.Override(UnityEngine.Rendering.Universal.TonemappingMode.ACES);
            var ca = Ensure<UnityEngine.Rendering.Universal.ColorAdjustments>(baseProfile);
            ca.saturation.Override(-35f);
            ca.contrast.Override(12f);
            ca.postExposure.Override(0.25f);
            var bloom = Ensure<UnityEngine.Rendering.Universal.Bloom>(baseProfile);
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.8f);
            bloom.scatter.Override(0.7f);
            EditorUtility.SetDirty(baseProfile);

            var powerProfile = GetOrCreateProfile(RenderingDir + "/PowerSurge.asset");
            var pca = Ensure<UnityEngine.Rendering.Universal.ColorAdjustments>(powerProfile);
            pca.saturation.Override(30f);
            pca.contrast.Override(22f);
            pca.postExposure.Override(0.35f);
            pca.colorFilter.Override(Color.white);
            var pbloom = Ensure<UnityEngine.Rendering.Universal.Bloom>(powerProfile);
            pbloom.threshold.Override(1.0f);
            pbloom.intensity.Override(1.6f);
            pbloom.scatter.Override(0.78f);
            pbloom.tint.Override(Color.white);
            var vig = Ensure<UnityEngine.Rendering.Universal.Vignette>(powerProfile);
            vig.intensity.Override(0.38f);
            vig.smoothness.Override(0.45f);
            var chroma = Ensure<UnityEngine.Rendering.Universal.ChromaticAberration>(powerProfile);
            chroma.intensity.Override(0.25f);
            EditorUtility.SetDirty(powerProfile);
            AssetDatabase.SaveAssets();

            var baseGo = new GameObject("Base Look (Volume)");
            var baseVol = baseGo.AddComponent<UnityEngine.Rendering.Volume>();
            baseVol.isGlobal = true;
            baseVol.sharedProfile = baseProfile;

            var powerGo = new GameObject("Power Volume");
            var powerVol = powerGo.AddComponent<UnityEngine.Rendering.Volume>();
            powerVol.isGlobal = true;
            powerVol.priority = 10f;
            powerVol.weight = 0f;
            powerVol.sharedProfile = powerProfile;
            return powerVol;
        }

        static Material GetOrCreateShaderMaterial(string name, string shader)
        {
            var path = $"{MaterialsDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        static UnityEngine.Rendering.VolumeProfile GetOrCreateProfile(string path)
        {
            var p = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.VolumeProfile>(path);
            if (p != null) return p;
            p = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            AssetDatabase.CreateAsset(p, path);
            return p;
        }

        static T Ensure<T>(UnityEngine.Rendering.VolumeProfile profile) where T : UnityEngine.Rendering.VolumeComponent
        {
            if (profile.TryGet<T>(out var c)) return c;
            c = profile.Add<T>(false);
            AssetDatabase.AddObjectToAsset(c, profile); // volume components are sub-assets of the profile
            return c;
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
