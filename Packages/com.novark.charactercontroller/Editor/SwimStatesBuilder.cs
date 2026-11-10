using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Swimming (SwimController): a "Swim" blend tree on SwimSpeed with the treading-water idle and forward
    /// crawl, entered from Any State on the Swimming bool, left for Locomotion when it clears. Safe to re-run.
    /// </summary>
    public static class SwimStatesBuilder
    {
        private const string Tag = "[SwimStatesBuilder]";
        private const string StateName = "Swim";

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("SwimIdle", p.SwimIdle), ("SwimForward", p.SwimForward));
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Swimming", missing);
                return false;
            }
            Param(controller, "Swimming", AnimatorControllerParameterType.Bool);
            Param(controller, "SwimSpeed", AnimatorControllerParameterType.Float);

            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && t.destinationState.name == StateName).ToArray())
                sm.RemoveAnyStateTransition(t);
            var old = State(sm, StateName);
            if (old != null)
            {
                if (old.motion is BlendTree bt && AssetDatabase.IsSubAsset(bt)) Object.DestroyImmediate(bt, true);
                sm.RemoveState(old);
            }

            var tree = new BlendTree
            {
                name = StateName, blendType = BlendTreeType.Simple1D, blendParameter = "SwimSpeed",
                useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(p.SwimIdle, 0f);
            tree.AddChild(p.SwimForward, 1.6f);
            tree.AddChild(p.SwimForward, 2.8f);
            var children = tree.children;
            children[2].timeScale = 1.5f; // sprint-swim: faster strokes
            tree.children = children;

            var state = sm.AddState(StateName, new Vector3(-1050f, 300f));
            state.motion = tree;
            state.writeDefaultValues = true;
            state.tag = "Swim";

            var enter = sm.AddAnyStateTransition(state);
            enter.canTransitionToSelf = false;
            enter.hasExitTime = false;
            enter.duration = 0.3f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Swimming");

            Cond(state, loco, 0.3f, (AnimatorConditionMode.IfNot, 0f, "Swimming"));
            EditorUtility.SetDirty(controller);
            return true;
        }

        /// <summary>Soft round droplet material for splash particles (unlit, alpha blended, any pipeline).</summary>
        public static Material SplashMaterial(CharacterSetupProfile p)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(p.SplashMaterialPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("HDRP/Unlit");
                if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                EnsureFolderFor(p.SplashMaterialPath);
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, p.SplashMaterialPath);
            }
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            var dot = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
            if (dot != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", dot);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", dot);
            }
            var tint = new Color(0.9f, 0.96f, 1f, 0.9f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        public static void SetUpPlayer(GameObject root, CharacterSetupProfile p)
        {
            var swim = Kit.Ensure<SwimController>(root);
            swim.Controller = root.GetComponent<ThirdPersonController>();
            swim.Parkour = root.GetComponent<ParkourController>();
            var effects = Kit.Ensure<WaterEffects>(root);
            effects.SplashMaterial = SplashMaterial(p);
            swim.Effects = effects;
        }
    }
}
