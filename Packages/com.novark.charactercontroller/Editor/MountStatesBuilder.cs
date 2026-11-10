using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// "Mounted" state for CharacterMountController: entered from Any State while Mounted is on, back to
    /// Locomotion when it clears. MountPose is free for vehicle specific poses (add states that test it).
    /// Safe to re-run.
    /// </summary>
    public static class MountStatesBuilder
    {
        private const string Tag = "[MountStatesBuilder]";
        public const string StateName = "Mounted";

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("MountIdle", p.MountIdle));
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Mounts", missing);
                return false;
            }
            Param(controller, "Mounted", AnimatorControllerParameterType.Bool);
            Param(controller, "MountPose", AnimatorControllerParameterType.Int);

            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && t.destinationState.name == StateName).ToArray())
                sm.RemoveAnyStateTransition(t);
            var state = State(sm, StateName) ?? sm.AddState(StateName, new Vector3(-1050f, 400f));
            ClearTransitions(state);
            state.motion = p.MountIdle;
            state.tag = "Mounted";
            state.writeDefaultValues = true;

            var enter = sm.AddAnyStateTransition(state);
            enter.canTransitionToSelf = false;
            enter.hasExitTime = false;
            enter.duration = 0.25f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Mounted");
            Cond(state, loco, 0.25f, (AnimatorConditionMode.IfNot, 0f, "Mounted"));
            EditorUtility.SetDirty(controller);
            return true;
        }
    }
}
