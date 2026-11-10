using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Crouch blend tree with the UAL crouch idle and walk (CC0), played so the stride matches the
    /// controller's crouch speed (the old Mixamo crouch walk covered only 0.55 m/s against 1.5 m/s of
    /// movement, so the feet slid). Safe to re-run.
    /// </summary>
    public static class CrouchStatesBuilder
    {
        private const string Tag = "[CrouchStatesBuilder]";
        private const string UalPath = "Assets/Art/Animations/UAL/UAL1_Standard.fbx";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";

        /// <summary>Crouch move speed (m/s) for the controller and the blend tree.</summary>
        public const float CrouchSpeed = 1.2f;
        /// <summary>Ground speed of UAL Crouch_Fwd_Loop at normal playback, measured from its foot travel.</summary>
        public const float ClipSpeed = 1.0f;

        [MenuItem("Tools/Character Controller/Upgrade Crouch (UAL)")]
        public static void UpgradeDefault()
        {
            Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            SetUpPlayer();
        }

        public static void Apply(AnimatorController controller)
        {
            var clips = AssetDatabase.LoadAllAssetRepresentationsAtPath(UalPath).OfType<AnimationClip>().ToList();
            var idle = clips.FirstOrDefault(c => c.name == "Crouch_Idle_Loop");
            var walk = clips.FirstOrDefault(c => c.name == "Crouch_Fwd_Loop");
            var state = controller != null
                ? controller.layers[0].stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == "Crouch")
                : null;
            if (state == null || !(state.motion is BlendTree tree) || idle == null || walk == null)
            {
                Debug.LogError($"{Tag} Crouch state, its blend tree or the UAL crouch clips are missing.");
                return;
            }

            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new ChildMotion { motion = idle, threshold = 0f, timeScale = 1f },
                new ChildMotion { motion = walk, threshold = CrouchSpeed, timeScale = CrouchSpeed / ClipSpeed },
            };
            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Crouch uses UAL clips at {CrouchSpeed} m/s.");
        }

        public static void SetUpPlayer()
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var tpc = root.GetComponent<ThirdPersonController>();
                if (tpc != null) tpc.CrouchSpeed = CrouchSpeed;
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
