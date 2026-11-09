using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Swimming (SwimController): a "Swim" blend tree on SwimSpeed with the UAL (CC0) treading-water idle and
    /// forward crawl, entered from Any State on the Swimming bool, left for Locomotion when it clears.
    /// Adds SwimController to the Player prefab. Safe to re-run.
    /// </summary>
    public static class SwimStatesBuilder
    {
        private const string Tag = "[SwimStatesBuilder]";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";
        private const string UalPath = "Assets/Art/Animations/UAL/UAL1_Standard.fbx";
        private const string StateName = "Swim";

        [MenuItem("Tools/Character Controller/Add Swimming")]
        public static void BuildAll()
        {
            Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            SetUpPlayer();
        }

        public static void Apply(AnimatorController controller)
        {
            var clips = AssetDatabase.LoadAllAssetRepresentationsAtPath(UalPath).OfType<AnimationClip>().ToList();
            var idle = clips.FirstOrDefault(c => c.name == "Swim_Idle_Loop");
            var fwd = clips.FirstOrDefault(c => c.name == "Swim_Fwd_Loop");
            if (controller == null || idle == null || fwd == null)
            {
                Debug.LogError($"{Tag} Controller or UAL swim clips missing.");
                return;
            }
            if (controller.parameters.All(p => p.name != "Swimming")) controller.AddParameter("Swimming", AnimatorControllerParameterType.Bool);
            if (controller.parameters.All(p => p.name != "SwimSpeed")) controller.AddParameter("SwimSpeed", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null) return;
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && t.destinationState.name == StateName).ToArray())
                sm.RemoveAnyStateTransition(t);
            var old = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == StateName);
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
            tree.AddChild(idle, 0f);
            tree.AddChild(fwd, 1.6f);
            tree.AddChild(fwd, 2.8f);
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

            var exit = state.AddTransition(loco);
            exit.hasExitTime = false;
            exit.duration = 0.3f;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "Swimming");

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Swim state added.");
        }

        public static void SetUpPlayer()
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var swim = root.GetComponent<SwimController>() ?? root.AddComponent<SwimController>();
                swim.Controller = root.GetComponent<ThirdPersonController>();
                swim.Parkour = root.GetComponent<ParkourController>();
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
                Debug.Log($"{Tag} SwimController on {PlayerPrefab}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
