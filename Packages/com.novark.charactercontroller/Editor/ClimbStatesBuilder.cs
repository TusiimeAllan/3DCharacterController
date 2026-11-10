using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Ledge hanging and ladder climbing (ClimbController):
    /// - makes the hang, shimmy and ladder clips loop with the body (not the recorded root) driving orientation;
    /// - adds base-layer states entered from Any State: Hang Idle / Hang Shimmy R / Hang Shimmy L on the
    ///   Hanging bool and HangMove float, Ladder on OnLadder (playback speed from LadderSpeed).
    /// Safe to re-run.
    /// </summary>
    public static class ClimbStatesBuilder
    {
        private const string Tag = "[ClimbStatesBuilder]";
        private static readonly string[] States = { "Hang Idle", "Hang Shimmy R", "Hang Shimmy L", "Ladder" };

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("HangIdle", p.HangIdle), ("HangShimmy", p.HangShimmy), ("LadderClimb", p.LadderClimb));
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Ledges and ladders", missing);
                return false;
            }
            AnimationClip Prepare(AnimationClip c)
            {
                EnsureImport(c, loop: true, bodyDriven: true);
                return Reload(c);
            }
            var hang = Prepare(p.HangIdle);
            var shimmy = Prepare(p.HangShimmy);
            var ladder = Prepare(p.LadderClimb);

            Param(controller, "Hanging", AnimatorControllerParameterType.Bool);
            Param(controller, "HangMove", AnimatorControllerParameterType.Float);
            Param(controller, "OnLadder", AnimatorControllerParameterType.Bool);
            Param(controller, "LadderSpeed", AnimatorControllerParameterType.Float);

            var fall = State(sm, "Fall") ?? loco;
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && States.Contains(t.destinationState.name)).ToArray())
                sm.RemoveAnyStateTransition(t);
            AnimatorState S(string name, Motion motion, Vector3 pos)
            {
                var s = State(sm, name) ?? sm.AddState(name, pos);
                ClearTransitions(s);
                s.motion = motion;
                s.writeDefaultValues = true;
                s.tag = "Climb";
                s.mirror = false;
                s.speedParameterActive = false;
                return s;
            }

            var idle = S("Hang Idle", hang, new Vector3(-1050f, 0f));
            var right = S("Hang Shimmy R", shimmy, new Vector3(-1050f, 60f));
            var left = S("Hang Shimmy L", shimmy, new Vector3(-1050f, 120f));
            left.mirror = true; // the clip shimmies to the right
            var climb = S("Ladder", ladder, new Vector3(-1050f, 200f));
            climb.speedParameter = "LadderSpeed";
            climb.speedParameterActive = true;

            void Any(AnimatorState to, float duration, params (AnimatorConditionMode mode, float threshold, string param)[] conds)
            {
                var t = sm.AddAnyStateTransition(to);
                t.canTransitionToSelf = false;
                t.hasExitTime = false;
                t.duration = duration;
                foreach (var (mode, threshold, param) in conds) t.AddCondition(mode, threshold, param);
            }
            Any(idle, 0.2f, (AnimatorConditionMode.If, 0f, "Hanging"), (AnimatorConditionMode.Greater, -0.1f, "HangMove"),
                (AnimatorConditionMode.Less, 0.1f, "HangMove"));
            Any(right, 0.2f, (AnimatorConditionMode.If, 0f, "Hanging"), (AnimatorConditionMode.Greater, 0.1f, "HangMove"));
            Any(left, 0.2f, (AnimatorConditionMode.If, 0f, "Hanging"), (AnimatorConditionMode.Less, -0.1f, "HangMove"));
            Any(climb, 0.2f, (AnimatorConditionMode.If, 0f, "OnLadder"));

            // Let go: fall (climbing up is a parkour state, entered from Any State).
            foreach (var s in new[] { idle, right, left })
                Cond(s, fall, 0.2f, (AnimatorConditionMode.IfNot, 0f, "Hanging"));
            Cond(climb, loco, 0.2f, (AnimatorConditionMode.IfNot, 0f, "OnLadder"));

            EditorUtility.SetDirty(controller);
            return true;
        }

        public static void SetUpPlayer(GameObject root, CharacterSetupProfile p)
        {
            var climb = Kit.Ensure<ClimbController>(root);
            climb.Controller = root.GetComponent<ThirdPersonController>();
            climb.Parkour = root.GetComponent<ParkourController>();
        }
    }
}
