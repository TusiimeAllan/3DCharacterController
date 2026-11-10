using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// "Slope Slide" state for sliding down slopes steeper than the slope limit: a slide clip held at its
    /// lowest pose (state speed 0), entered from Any State on the SlopeSlide bool and left for Locomotion when
    /// it clears. Safe to re-run.
    /// </summary>
    public static class SlopeStatesBuilder
    {
        private const string Tag = "[SlopeStatesBuilder]";
        private const string StateName = "Slope Slide";

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("SlopeSlide", p.SlopeSlide));
            var loco = State(controller, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Steep slope slide", missing);
                return false;
            }
            Param(controller, "SlopeSlide", AnimatorControllerParameterType.Bool);

            var sm = controller.layers[0].stateMachine;
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && t.destinationState.name == StateName).ToArray())
                sm.RemoveAnyStateTransition(t);
            var state = State(sm, StateName) ?? sm.AddState(StateName, new Vector3(-800f, 500f));
            ClearTransitions(state);
            state.motion = p.SlopeSlide;
            state.speed = 0f;
            state.cycleOffset = p.SlopeSlidePoseTime;
            state.writeDefaultValues = true;

            var enter = sm.AddAnyStateTransition(state);
            enter.canTransitionToSelf = false;
            enter.hasExitTime = false;
            enter.duration = 0.2f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "SlopeSlide");

            Cond(state, loco, 0.25f, (AnimatorConditionMode.IfNot, 0f, "SlopeSlide"));
            EditorUtility.SetDirty(controller);
            return true;
        }
    }
}
