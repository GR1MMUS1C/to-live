using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>
    /// Import rules for the Meshy-rigged hero FBX files in Art/Characters/Hero (like an Unreal
    /// FBX import preset that always runs). Every file becomes a Humanoid with the same explicit
    /// bone map, so the walk/run clips retarget onto any hero body (like an IK Retargeter between
    /// two skeletons that share a rig asset).
    /// Meshy's skeleton has no Spine bone and no fingers: Chest is mapped to Spine and UpperChest to Chest.
    /// </summary>
    public class HeroImportSettings : AssetPostprocessor
    {
        public const string HeroDir = "Assets/_Project/Art/Characters/Hero/";

        // Meshy rigs at height_meters = 1.85 and its FBX file units already land in metres:
        // measured 1.85 m tall, feet at y = 0, so no extra scale is needed.
        const float GlobalScale = 1f;

        // Unity human bone (HumanBodyBones) -> Meshy bone name.
        static readonly (HumanBodyBones human, string bone)[] k_Map =
        {
            (HumanBodyBones.Hips, "Hips"),
            (HumanBodyBones.Spine, "Chest"),
            (HumanBodyBones.Chest, "UpperChest"),
            (HumanBodyBones.Neck, "Neck"),
            (HumanBodyBones.Head, "Head"),
            (HumanBodyBones.LeftShoulder, "LeftShoulder"),
            (HumanBodyBones.LeftUpperArm, "LeftUpperArm"),
            (HumanBodyBones.LeftLowerArm, "LeftLowerArm"),
            (HumanBodyBones.LeftHand, "LeftHand"),
            (HumanBodyBones.RightShoulder, "RightShoulder"),
            (HumanBodyBones.RightUpperArm, "RightUpperArm"),
            (HumanBodyBones.RightLowerArm, "RightLowerArm"),
            (HumanBodyBones.RightHand, "RightHand"),
            (HumanBodyBones.LeftUpperLeg, "LeftUpperLeg"),
            (HumanBodyBones.LeftLowerLeg, "LeftLowerLeg"),
            (HumanBodyBones.LeftFoot, "LeftFoot"),
            (HumanBodyBones.LeftToes, "LeftToes"),
            (HumanBodyBones.RightUpperLeg, "RightUpperLeg"),
            (HumanBodyBones.RightLowerLeg, "RightLowerLeg"),
            (HumanBodyBones.RightFoot, "RightFoot"),
            (HumanBodyBones.RightToes, "RightToes"),
        };

        static bool IsHero(string path) => path.StartsWith(HeroDir) && path.ToLowerInvariant().EndsWith(".fbx");
        static bool IsClip(string path) => path.Contains("_Walk") || path.Contains("_Run");

        void OnPreprocessModel()
        {
            if (!IsHero(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = GlobalScale;
            importer.useFileScale = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None; // ToLiveSetup builds the URP materials
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = IsClip(assetPath);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            var human = new List<HumanBone>();
            foreach (var (h, bone) in k_Map)
            {
                var hb = new HumanBone { humanName = HumanTrait.BoneName[(int)h], boneName = bone };
                hb.limit.useDefaultValues = true;
                human.Add(hb);
            }
            var d = importer.humanDescription; // keeps the skeleton (rest pose) Unity read on the last import
            // The very first import has no skeleton yet, so Unity auto-maps; reimport once to apply the map.
            if (d.skeleton == null || d.skeleton.Length == 0) s_NeedsSecondPass.Add(assetPath);
            d.human = human.ToArray();
            importer.humanDescription = d;
        }

        static readonly HashSet<string> s_NeedsSecondPass = new HashSet<string>();

        void OnPreprocessAnimation()
        {
            if (!IsHero(assetPath) || !IsClip(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            var clips = importer.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.loopTime = true;
                c.loopPose = true;
                // Bake turning and height into the pose; forward travel stays root motion, which the
                // Animator ignores, so the character runs on the spot and the ECS body moves it.
                c.lockRootRotation = true;
                c.keepOriginalOrientation = true;
                c.lockRootHeightY = true;
                c.keepOriginalPositionY = true;
                c.lockRootPositionXZ = false;
            }
            importer.clipAnimations = clips;
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!IsHero(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            if (s_NeedsSecondPass.Remove(assetPath))
            {
                var path = assetPath;
                EditorApplication.delayCall += () => AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                return;
            }
            var avatar = root.GetComponent<Animator>() != null ? root.GetComponent<Animator>().avatar : null;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
                Debug.LogWarning($"[ToLive] Hero import: {assetPath} has no valid humanoid avatar (skeleton bones: {importer.humanDescription.skeleton?.Length ?? 0}).");
        }
    }
}
