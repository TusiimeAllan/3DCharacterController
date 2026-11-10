using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Crouch blend tree (idle and walk), the walk played so its stride matches the controller's crouch speed
    /// (a clip covering 1.0 m/s under a body moving 1.2 m/s would slide its feet). Safe to re-run.
    /// </summary>
    public static class CrouchStatesBuilder
    {
        private const string Tag = "[CrouchStatesBuilder]";

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("CrouchIdle", p.CrouchIdle), ("CrouchWalk", p.CrouchWalk));
            var state = State(controller, "Crouch");
            if (missing.Length > 0 || state == null || !(state.motion is BlendTree tree))
            {
                if (missing.Length > 0) Skip(Tag, "Crouch", missing);
                return false;
            }

            float speed = Mathf.Max(0.1f, p.CrouchWalkThreshold);
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new ChildMotion { motion = p.CrouchIdle, threshold = 0f, timeScale = 1f },
                new ChildMotion { motion = p.CrouchWalk, threshold = speed, timeScale = speed / Mathf.Max(0.1f, p.CrouchClipSpeed) },
            };
            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            return true;
        }

        public static void SetUpPlayer(GameObject root, CharacterSetupProfile p)
        {
            var tpc = root.GetComponent<ThirdPersonController>();
            if (tpc != null) tpc.CrouchSpeed = p.CrouchWalkThreshold;
        }
    }
}
