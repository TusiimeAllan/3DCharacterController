using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Adds a Strafe state: a 2D freeform directional blend tree on StrafeX / StrafeZ (velocity in the
    /// character's frame) with idle, walk and run in four directions (backwards is the forward cycle played
    /// in reverse).
    ///   Locomotion <-> Strafe on the Strafing bool.
    /// Strafe gets a copy of every other exit Locomotion has (jump, fall, crouch, actions, emotes, push), so
    /// strafing never blocks them. Standing turns: Strafe -> Strafe Turn L / R while StrafeTurn is -1 / 1.
    /// Safe to re-run.
    /// </summary>
    public static class StrafeStatesBuilder
    {
        private const string Tag = "[StrafeStatesBuilder]";
        public const string StateName = "Strafe";
        private const float EnterDuration = 0.2f;
        private const float ExitDuration = 0.25f;
        private static readonly (string state, int dir)[] Turns = { ("Strafe Turn L", -1), ("Strafe Turn R", 1) };

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var missing = Missing(("StrafeWalkForward", p.StrafeWalkForward), ("StrafeWalkLeft", p.StrafeWalkLeft),
                ("StrafeWalkRight", p.StrafeWalkRight), ("StrafeRunForward", p.StrafeRunForward),
                ("StrafeRunLeft", p.StrafeRunLeft), ("StrafeRunRight", p.StrafeRunRight));
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Strafe", missing);
                return false;
            }

            // Cycles must loop.
            AnimationClip Loop(AnimationClip c)
            {
                EnsureImport(c, loop: true, bodyDriven: false);
                return Reload(c);
            }
            var walk = (fwd: Loop(p.StrafeWalkForward), left: Loop(p.StrafeWalkLeft), right: Loop(p.StrafeWalkRight));
            var run = (fwd: Loop(p.StrafeRunForward), left: Loop(p.StrafeRunLeft), right: Loop(p.StrafeRunRight));

            Param(controller, "Strafing", AnimatorControllerParameterType.Bool);
            Param(controller, "StrafeX", AnimatorControllerParameterType.Float);
            Param(controller, "StrafeZ", AnimatorControllerParameterType.Float);
            Param(controller, "StrafeTurn", AnimatorControllerParameterType.Int);

            // Same idle as Locomotion, so stepping in and out of strafe while standing does not pop.
            var idle = (loco.motion as BlendTree)?.children.OrderBy(c => c.threshold).Select(c => c.motion)
                .OfType<AnimationClip>().FirstOrDefault();

            var strafe = State(sm, StateName) ?? sm.AddState(StateName, new Vector3(250f, -120f));
            if (strafe.motion is BlendTree old && AssetDatabase.IsSubAsset(old)) Object.DestroyImmediate(old, true);

            var tree = new BlendTree
            {
                name = "Strafe",
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = "StrafeX",
                blendParameterY = "StrafeZ",
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);

            // Positions are each clip's real velocity, so the blend matches the speed the controller moves at.
            if (idle != null) tree.AddChild(idle, Vector2.zero);
            foreach (var (fwd, left, right) in new[] { walk, run })
            {
                float fwdSpeed = Mathf.Abs(fwd.averageSpeed.z);
                tree.AddChild(fwd, new Vector2(0f, fwdSpeed));
                tree.AddChild(fwd, new Vector2(0f, -fwdSpeed)); // reversed below: backwards
                tree.AddChild(left, new Vector2(-Mathf.Abs(left.averageSpeed.x), 0f));
                tree.AddChild(right, new Vector2(Mathf.Abs(right.averageSpeed.x), 0f));
            }
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
                if (children[i].position.y < -0.01f) children[i].timeScale = -1f;
            tree.children = children;

            strafe.motion = tree;
            strafe.writeDefaultValues = true;

            var turnClips = new[] { p.StrafeTurnLeft, p.StrafeTurnRight };
            for (int i = 0; i < Turns.Length; i++)
            {
                if (turnClips[i] == null)
                {
                    Debug.LogWarning($"{Tag} No strafe turn clip for {Turns[i].state}, standing turns will slide.");
                    continue;
                }
                var turn = State(sm, Turns[i].state) ?? sm.AddState(Turns[i].state, new Vector3(500f, -180f + i * 120f));
                turn.motion = turnClips[i];
                turn.writeDefaultValues = true;
            }
            SyncExits(controller);

            EditorUtility.SetDirty(controller);
            return true;
        }

        /// <summary>
        /// Rebuilds Strafe's transitions: Locomotion's exits first (same order and conditions, so jumps,
        /// actions and emotes win), then back to Locomotion when Strafing turns off.
        /// </summary>
        public static void SyncExits(AnimatorController controller)
        {
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            var strafe = State(sm, StateName);
            if (loco == null || strafe == null) return;

            foreach (var t in loco.transitions.Where(t => t.destinationState == strafe).ToArray()) loco.RemoveTransition(t);
            ClearTransitions(strafe);

            foreach (var src in loco.transitions)
            {
                if (src.destinationState == null) continue;
                var t = strafe.AddTransition(src.destinationState);
                t.hasExitTime = src.hasExitTime;
                t.exitTime = src.exitTime;
                t.duration = src.duration;
                t.offset = src.offset;
                t.hasFixedDuration = src.hasFixedDuration;
                t.interruptionSource = src.interruptionSource;
                foreach (var c in src.conditions) t.AddCondition(c.mode, c.threshold, c.parameter);
            }

            // Standing turns, after the copied exits so a jump or action still wins.
            foreach (var (stateName, dir) in Turns)
            {
                var turn = State(sm, stateName);
                if (turn == null) continue;
                ClearTransitions(turn);
                var into = strafe.AddTransition(turn);
                into.hasExitTime = false;
                into.duration = 0.1f;
                into.AddCondition(AnimatorConditionMode.Equals, dir, "StrafeTurn");
                // Ends when the controller finishes (or cancels) the turn; a chained turn can cut in.
                var done = turn.AddTransition(strafe);
                done.hasExitTime = false;
                done.duration = 0.15f;
                done.interruptionSource = TransitionInterruptionSource.Destination;
                done.AddCondition(AnimatorConditionMode.NotEqual, dir, "StrafeTurn");
            }

            var back = strafe.AddTransition(loco);
            back.hasExitTime = false;
            back.duration = ExitDuration;
            back.AddCondition(AnimatorConditionMode.IfNot, 0f, "Strafing");

            var enter = loco.AddTransition(strafe);
            enter.hasExitTime = false;
            enter.duration = EnterDuration;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Strafing");
            EditorUtility.SetDirty(controller);
        }
    }
}
