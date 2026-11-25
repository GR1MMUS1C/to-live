using System.IO;
using System.Linq;
using ToLive.Player;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Hero look: URP materials (with a packed metallic/smoothness map made from Meshy's
    /// metallic-roughness texture), the hero Animator Controller and the hero prefabs, and the
    /// HeroPresenter in Main.unity. Import settings for the FBX files live in HeroImportSettings.
    /// </summary>
    public static partial class ToLiveSetup
    {
        const string HeroArtDir = "Assets/_Project/Art/Characters/Hero";
        const string HeroTexDir = HeroArtDir + "/Textures";
        const string HeroControllerPath = HeroArtDir + "/Hero.controller";
        const string HeroPrefabPath = PrefabsDir + "/Hero.prefab";
        const string HeroDominancePrefabPath = PrefabsDir + "/HeroDominance.prefab";

        [MenuItem("Tools/To Live/Build Hero Look", priority = 2)]
        public static void BuildHeroLookMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureFolders();
            BuildHeroLook(out var hero, out var dominance);
            if (File.Exists(MainScenePath))
            {
                var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
                var look = Object.FindAnyObjectByType<ToLive.Power.PowerLook>();
                if (look != null)
                {
                    AttachHeroPresenter(look.gameObject, hero, dominance);
                    EditorSceneManager.SaveScene(scene);
                }
                else Debug.LogWarning("[ToLive] Main.unity has no Power Look object; run Setup Project.");
            }
            Debug.Log($"[ToLive] Hero look built (dominance model: {(dominance != null ? "yes" : "not yet")}).");
        }

        /// <summary>Adds (or refreshes) the HeroPresenter next to PowerLook.</summary>
        static void AttachHeroPresenter(GameObject lookGo, GameObject hero, GameObject dominance)
        {
            if (hero == null) return;
            var presenter = lookGo.GetComponent<HeroPresenter>();
            if (presenter == null) presenter = lookGo.AddComponent<HeroPresenter>();
            presenter.HeroPrefab = hero;
            presenter.DominancePrefab = dominance;
            EditorUtility.SetDirty(presenter);
        }

        /// <summary>Builds everything the hero needs. Returns null prefabs when the FBX is missing.</summary>
        static void BuildHeroLook(out GameObject hero, out GameObject dominance)
        {
            hero = dominance = null;
            var baseFbx = HeroArtDir + "/Hero_Base.fbx";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(baseFbx) == null)
            {
                Debug.LogWarning($"[ToLive] {baseFbx} is missing; the players stay capsules.");
                return;
            }
            var controller = BuildHeroController();
            hero = BuildHeroPrefab(baseFbx, BuildHeroMaterial("Hero"), controller, HeroPrefabPath);
            var domFbx = HeroArtDir + "/Hero_Dominance.fbx";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(domFbx) != null)
                dominance = BuildHeroPrefab(domFbx, BuildHeroMaterial("HeroDominance"), controller, HeroDominancePrefabPath);
            AssetDatabase.SaveAssets();
        }

        // ---------- materials ----------
        static Material BuildHeroMaterial(string form)
        {
            var baseMap = ImportTexture($"{HeroTexDir}/{form}_BaseColor.png", TextureImporterType.Default, true, false);
            var normal = ImportTexture($"{HeroTexDir}/{form}_Normal.png", TextureImporterType.NormalMap, false, false);
            var metalSmooth = BuildMetalSmooth(form);

            var path = $"{MaterialsDir}/M_{form}.mat";
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(lit); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = lit;
            mat.SetTexture("_BaseMap", baseMap);
            mat.SetColor("_BaseColor", new Color(0.92f, 0.92f, 0.92f, 1f)); // HeroPresenter overrides it per frame
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
            mat.EnableKeyword("_NORMALMAP");
            mat.SetTexture("_MetallicGlossMap", metalSmooth);
            mat.SetFloat("_Smoothness", 1f);                 // scales the map's alpha
            mat.SetFloat("_SmoothnessTextureChannel", 0f);   // smoothness from the metallic map's alpha
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Texture2D ImportTexture(string path, TextureImporterType type, bool srgb, bool readable)
        {
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp == null) { Debug.LogError($"[ToLive] Missing texture {path}"); return null; }
            if (imp.textureType != type || imp.sRGBTexture != srgb || imp.isReadable != readable || imp.maxTextureSize != 2048)
            {
                imp.textureType = type;
                imp.sRGBTexture = srgb;
                imp.isReadable = readable;
                imp.maxTextureSize = 2048;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// URP Lit wants metallic in R and smoothness in A. Meshy ships glTF metallic-roughness
        /// (roughness in G, metallic in B), so: R = metallic, A = 1 - roughness.
        /// </summary>
        static Texture2D BuildMetalSmooth(string form)
        {
            var srcPath = $"{HeroTexDir}/{form}_MetallicRoughness.png";
            var outPath = $"{HeroTexDir}/{form}_MetalSmooth.png";
            var src = ImportTexture(srcPath, TextureImporterType.Default, false, true);
            var px = src.GetPixels32();
            var outPx = new Color32[px.Length];
            for (var i = 0; i < px.Length; i++)
                outPx[i] = new Color32(px[i].b, 0, 0, (byte)(255 - px[i].g));
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(outPx);
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            ImportTexture(srcPath, TextureImporterType.Default, false, false); // back to non-readable
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
            imp.sRGBTexture = false;
            imp.alphaSource = TextureImporterAlphaSource.FromInput;
            imp.alphaIsTransparency = false;
            imp.maxTextureSize = 2048;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        // ---------- animator ----------
        static AnimationClip LoadClip(string fbx) =>
            AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));

        /// <summary>
        /// Like an AnimBP state machine: Idle (frozen walk frame) / Locomotion (1D blend on Speed:
        /// walk -> run) / Air (frozen run frame, held until there are flight clips).
        /// </summary>
        static AnimatorController BuildHeroController()
        {
            var walk = LoadClip(HeroArtDir + "/Hero_Walk.fbx");
            var run = LoadClip(HeroArtDir + "/Hero_Run.fbx");
            // Rebuilt in place (not deleted), so the hero prefabs keep a live reference to it.
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(HeroControllerPath);
            if (ac == null) ac = AnimatorController.CreateAnimatorControllerAtPath(HeroControllerPath);
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(HeroControllerPath))
                if (sub is BlendTree) Object.DestroyImmediate(sub, true);
            while (ac.parameters.Length > 0) ac.RemoveParameter(0);
            var old = ac.layers[0].stateMachine;
            foreach (var cs in old.states) old.RemoveState(cs.state);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            var sm = ac.layers[0].stateMachine;

            // Natural speed of each clip (m/s) so the feet don't slide at the blend points.
            // Meshy's clips run on the spot (no root motion), so it is estimated from the stride.
            var walkSpeed = StrideSpeed(walk);
            var runSpeed = Mathf.Max(StrideSpeed(run), walkSpeed + 0.5f);

            var idle = sm.AddState("Idle", new Vector3(300, 0));
            idle.motion = walk;
            idle.speed = 0f;
            idle.cycleOffset = FeetTogetherTime(walk);

            var loco = sm.AddState("Locomotion", new Vector3(300, 120));
            var tree = new BlendTree { name = "WalkRun", blendType = BlendTreeType.Simple1D, blendParameter = "Speed", useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(tree, ac);
            tree.AddChild(walk, walkSpeed);
            tree.AddChild(run, runSpeed);
            loco.motion = tree;

            var air = sm.AddState("Air", new Vector3(560, 60));
            air.motion = run;
            air.speed = 0f;
            air.cycleOffset = FeetTogetherTime(run);
            sm.defaultState = idle;

            AddTransition(idle, loco, 0.15f, (AnimatorConditionMode.If, 0f, "Grounded"), (AnimatorConditionMode.Greater, 0.4f, "Speed"));
            AddTransition(loco, idle, 0.2f, (AnimatorConditionMode.Less, 0.25f, "Speed"));
            AddTransition(idle, air, 0.25f, (AnimatorConditionMode.IfNot, 0f, "Grounded"));
            AddTransition(loco, air, 0.25f, (AnimatorConditionMode.IfNot, 0f, "Grounded"));
            AddTransition(air, idle, 0.2f, (AnimatorConditionMode.If, 0f, "Grounded"));
            EditorUtility.SetDirty(ac);
            Debug.Log($"[ToLive] Hero controller: walk {walkSpeed:0.00} m/s, run {runSpeed:0.00} m/s, idle frame {idle.cycleOffset:0.00}, air frame {air.cycleOffset:0.00}");
            return ac;
        }

        static void AddTransition(AnimatorState from, AnimatorState to, float duration, params (AnimatorConditionMode mode, float threshold, string param)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            foreach (var c in conditions) t.AddCondition(c.mode, c.threshold, c.param);
        }

        /// <summary>
        /// Ground speed an in-place clip implies: a planted foot sweeps back its stride length in
        /// about half a cycle, so speed ~ stride / (length / 2).
        /// </summary>
        static float StrideSpeed(AnimationClip clip)
        {
            if (clip.averageSpeed.magnitude > 0.2f) return clip.averageSpeed.magnitude;
            var go = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HeroArtDir + "/Hero_Base.fbx"));
            try
            {
                var foot = go.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftFoot);
                float lo = float.MaxValue, hi = float.MinValue;
                for (var i = 0; i < 40; i++)
                {
                    clip.SampleAnimation(go, i / 40f * clip.length);
                    var z = go.transform.InverseTransformPoint(foot.position).z;
                    lo = Mathf.Min(lo, z); hi = Mathf.Max(hi, z);
                }
                return (hi - lo) / (clip.length * 0.5f);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>Normalized time where the feet are closest together (a neutral standing/flying pose).</summary>
        static float FeetTogetherTime(AnimationClip clip)
        {
            var go = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HeroArtDir + "/Hero_Base.fbx"));
            try
            {
                var anim = go.GetComponent<Animator>();
                var l = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var r = anim.GetBoneTransform(HumanBodyBones.RightFoot);
                float best = 0f, bestDist = float.MaxValue;
                for (var i = 0; i < 40; i++)
                {
                    var t = i / 40f;
                    clip.SampleAnimation(go, t * clip.length);
                    var d = Vector3.Distance(l.position, r.position);
                    if (d < bestDist) { bestDist = d; best = t; }
                }
                return best;
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---------- prefabs ----------
        static GameObject BuildHeroPrefab(string fbx, Material mat, RuntimeAnimatorController controller, string prefabPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = Path.GetFileNameWithoutExtension(prefabPath);
            var anim = go.GetComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false; // the ECS body moves the character; clips run on the spot
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.sharedMaterial = mat;
                smr.updateWhenOffscreen = false;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
