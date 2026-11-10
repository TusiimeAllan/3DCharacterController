using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Air states:
    ///   Locomotion -> Jump (takeoff) -> JumpLoop -> Fall (long drops)
    ///   any air state -> Land for normal landings, or Roll for hard ones.
    /// Land and Roll are tagged "Landing" so motion matching waits for them to finish.
    /// Edits the controller in place. Safe to re-run.
    /// </summary>
    public static class AirborneStatesBuilder
    {
        private const string Tag = "[AirborneStatesBuilder]";

        /// <summary>Landing faster than this (m/s) rolls; about a 3.7 m drop with the default gravity.</summary>
        public const float RollSpeed = 10.5f;
        /// <summary>Falling faster than this (m/s) switches the jump pose to the long-fall loop.</summary>
        public const float LongFallSpeed = 9f;
        private const float MinLandSpeed = 0.5f;

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("Jump", p.Jump), ("JumpLoop", p.JumpLoop), ("Land", p.Land), ("Roll", p.Roll));
            if (controller == null || missing.Length > 0)
            {
                Skip(Tag, "Jump loop, landing and roll", missing);
                return false;
            }

            Param(controller, "FallSpeed", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            var crouch = State(sm, "Crouch");
            var jump = State(sm, "Jump");
            var fall = State(sm, "Fall");
            if (loco == null || jump == null || fall == null)
            {
                Debug.LogError($"{Tag} Expected Locomotion, Jump and Fall states.");
                return false;
            }

            var loop = State(sm, "JumpLoop") ?? sm.AddState("JumpLoop", new Vector3(520f, 0f));
            var land = State(sm, "Land") ?? sm.AddState("Land", new Vector3(300f, 200f));
            var rollState = State(sm, "Roll") ?? sm.AddState("Roll", new Vector3(520f, 200f));

            jump.motion = p.Jump;
            loop.motion = p.JumpLoop;
            land.motion = p.Land;
            rollState.motion = p.Roll;
            land.tag = rollState.tag = "Landing";

            // Rebuild every transition out of the air and landing states.
            foreach (var s in new[] { jump, loop, fall, land, rollState }) ClearTransitions(s);

            // Takeoff into the airborne loop.
            Exit(jump, loop, 0.85f, 0.1f);

            // Long drop: switch to the falling loop once falling fast.
            foreach (var air in new[] { jump, loop })
                Cond(air, fall, 0.2f, (AnimatorConditionMode.Greater, LongFallSpeed, "FallSpeed"));

            // Touch down: soft landing or roll, picked by landing speed (FallSpeed > 0 only after a real fall).
            foreach (var air in new[] { jump, loop, fall })
            {
                Cond(air, land, 0.05f, (AnimatorConditionMode.If, 0f, "Grounded"),
                    (AnimatorConditionMode.Greater, MinLandSpeed, "FallSpeed"),
                    (AnimatorConditionMode.Less, RollSpeed, "FallSpeed"));
                Cond(air, rollState, 0.05f, (AnimatorConditionMode.If, 0f, "Grounded"),
                    (AnimatorConditionMode.Greater, RollSpeed - 0.01f, "FallSpeed"));
            }

            // Recover.
            Cond(land, jump, 0.05f, (AnimatorConditionMode.If, 0f, "Jump")); // bunny hop
            if (crouch != null) Cond(land, crouch, 0.15f, (AnimatorConditionMode.If, 0f, "Crouch"));
            Exit(land, loco, 0.7f, 0.2f);
            Exit(rollState, loco, 0.85f, 0.2f);

            EditorUtility.SetDirty(controller);
            return true;
        }
    }
}
