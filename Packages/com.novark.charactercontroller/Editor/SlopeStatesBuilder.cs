using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// "Slope Slide" state for sliding down slopes steeper than the slope limit: the MxM demo run slide
    /// held at its lowest pose (state speed 0), entered from Any State on the SlopeSlide bool and left for
    /// Locomotion when it clears. Safe to re-run.
    /// </summary>
    public static class SlopeStatesBuilder
    {
        private const string Tag = "[SlopeStatesBuilder]";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string SlideClip = "Assets/Plugins/MotionMatching/Demo/Animations/Events/RunSlide_ToRight_1.anim";
        private const string StateName = "Slope Slide";
        /// <summary>Normalized time of the low, feet-first part of the slide.</summary>
        private const float PoseTime = 0.42f;

        [MenuItem("Tools/Character Controller/Add Steep Slope Slide")]
        public static void BuildDefault() => Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));

        public static void Apply(AnimatorController controller)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(SlideClip);
            if (controller == null || clip == null)
            {
                Debug.LogWarning($"{Tag} Controller or slide clip missing, slope slide not added.");
                return;
            }
            if (controller.parameters.All(p => p.name != "SlopeSlide"))
                controller.AddParameter("SlopeSlide", AnimatorControllerParameterType.Bool);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null) return;
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && t.destinationState.name == StateName).ToArray())
                sm.RemoveAnyStateTransition(t);
            var state = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == StateName)
                        ?? sm.AddState(StateName, new Vector3(-800f, 500f));
            foreach (var t in state.transitions.ToArray()) state.RemoveTransition(t);
            state.motion = clip;
            state.speed = 0f;
            state.cycleOffset = PoseTime;
            state.writeDefaultValues = true;

            var enter = sm.AddAnyStateTransition(state);
            enter.canTransitionToSelf = false;
            enter.hasExitTime = false;
            enter.duration = 0.2f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "SlopeSlide");

            var exit = state.AddTransition(loco);
            exit.hasExitTime = false;
            exit.duration = 0.25f;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "SlopeSlide");

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} {StateName} added.");
        }
    }
}
