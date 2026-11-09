using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Upgrades the Player Animator Controller's air states with the Universal Animation Library (CC0):
    ///   Locomotion -> Jump (UAL Jump_Start) -> JumpLoop (UAL Jump_Loop) -> Fall (Mixamo Falling, long drops)
    ///   any air state -> Land (UAL Jump_Land) for normal landings, or Roll (UAL Roll) for hard ones.
    /// Land and Roll are tagged "Landing" so motion matching waits for them to finish.
    /// Edits the controller in place, so every reference to it stays valid. Safe to re-run.
    /// </summary>
    public static class AirborneStatesBuilder
    {
        private const string Tag = "[AirborneStatesBuilder]";
        private const string UalPath = "Assets/Art/Animations/UAL/UAL1_Standard.fbx";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";

        /// <summary>Landing faster than this (m/s) rolls; about a 3.7 m drop with the default gravity.</summary>
        public const float RollSpeed = 10.5f;
        /// <summary>Falling faster than this (m/s) switches the jump pose to the long-fall loop.</summary>
        public const float LongFallSpeed = 9f;
        private const float MinLandSpeed = 0.5f;

        [MenuItem("Tools/Character Controller/Upgrade Jump, Fall and Landing (UAL)")]
        public static void UpgradeDefault() =>
            Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));

        public static void Apply(AnimatorController controller)
        {
            var jumpStart = Ual("Jump_Start");
            var jumpLoop = Ual("Jump_Loop");
            var jumpLand = Ual("Jump_Land");
            var roll = Ual("Roll");
            if (controller == null || jumpStart == null || jumpLoop == null || jumpLand == null || roll == null)
            {
                Debug.LogError($"{Tag} Controller or UAL clips missing ({UalPath}), nothing changed.");
                return;
            }

            if (controller.parameters.All(p => p.name != "FallSpeed"))
                controller.AddParameter("FallSpeed", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            var crouch = State(sm, "Crouch");
            var jump = State(sm, "Jump");
            var fall = State(sm, "Fall");
            if (loco == null || jump == null || fall == null)
            {
                Debug.LogError($"{Tag} Expected Locomotion, Jump and Fall states.");
                return;
            }

            var loop = State(sm, "JumpLoop") ?? sm.AddState("JumpLoop", new Vector3(520f, 0f));
            var land = State(sm, "Land") ?? sm.AddState("Land", new Vector3(300f, 200f));
            var rollState = State(sm, "Roll") ?? sm.AddState("Roll", new Vector3(520f, 200f));

            jump.motion = jumpStart;
            loop.motion = jumpLoop;
            land.motion = jumpLand;
            rollState.motion = roll;
            land.tag = rollState.tag = "Landing";

            // Rebuild every transition out of the air and landing states.
            foreach (var s in new[] { jump, loop, fall, land, rollState }) Clear(s);

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
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Air states upgraded on {AssetDatabase.GetAssetPath(controller)}.");
        }

        private static AnimationClip Ual(string name) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(UalPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == name || c.name.EndsWith("|" + name));

        private static AnimatorState State(AnimatorStateMachine sm, string name) =>
            sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name);

        private static void Clear(AnimatorState s)
        {
            foreach (var t in s.transitions.ToArray()) s.RemoveTransition(t);
        }

        private static void Exit(AnimatorState from, AnimatorState to, float exitTime, float duration)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            t.duration = duration;
        }

        private static void Cond(AnimatorState from, AnimatorState to, float duration,
            params (AnimatorConditionMode mode, float threshold, string param)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            foreach (var (mode, threshold, param) in conditions) t.AddCondition(mode, threshold, param);
        }
    }
}
