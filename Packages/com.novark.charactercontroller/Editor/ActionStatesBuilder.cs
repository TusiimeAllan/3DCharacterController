using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Adds the CharacterActions states:
    ///   Any State -> Death A / Death B, back to Locomotion on revive
    ///   Locomotion -> SitEnter -> SitIdle -> SitExit
    ///   Locomotion -> Interact, PickUp, Jab, Cross, Repair (one-shots, tag "Action")
    ///   Locomotion -> Dance, Talk (loops, tag "Emote"), Push (loop while pushing, tag "Push")
    ///   UpperBody layer: Hit Chest / Hit Head, used when no locomotion host plays them.
    /// Edits the controller in place and is safe to re-run.
    /// </summary>
    public static class ActionStatesBuilder
    {
        private const string Tag = "[ActionStatesBuilder]";
        private const string HitLayerName = "UpperBody Hits";

        public static bool Apply(AnimatorController controller, CharacterSetupProfile p)
        {
            var clips = new (string state, AnimationClip clip)[]
            {
                ("Death A", p.DeathA), ("Death B", p.DeathB),
                ("SitEnter", p.SitEnter), ("SitIdle", p.SitIdle), ("SitExit", p.SitExit),
                ("Interact", p.Interact), ("PickUp", p.PickUp), ("Jab", p.Jab), ("Cross", p.Cross), ("Repair", p.Repair),
                ("Dance", p.Dance), ("Talk", p.Talk), ("Push", p.Push),
                ("Hit Chest", p.HitChest), ("Hit Head", p.HitHead),
            };
            var missing = clips.Where(c => c.clip == null).Select(c => c.state).ToArray();
            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (missing.Length > 0 || loco == null)
            {
                if (missing.Length > 0) Skip(Tag, "Actions", missing);
                return false;
            }
            AnimationClip C(string state) => clips.First(c => c.state == state).clip;

            Param(controller, "Action", AnimatorControllerParameterType.Int);
            Param(controller, "Dead", AnimatorControllerParameterType.Bool);
            Param(controller, "DeathIndex", AnimatorControllerParameterType.Int);
            Param(controller, "Sitting", AnimatorControllerParameterType.Bool);
            Param(controller, "Pushing", AnimatorControllerParameterType.Bool);
            Param(controller, "Hit", AnimatorControllerParameterType.Trigger);
            Param(controller, "HitIndex", AnimatorControllerParameterType.Int);

            // Remove what an earlier run added from Locomotion, then rebuild.
            string[] ours = { "Death A", "Death B", "SitEnter", "SitIdle", "SitExit", "Interact", "PickUp", "Jab",
                              "Cross", "Repair", "Dance", "Talk", "Push" };
            foreach (var t in loco.transitions.Where(t => t.destinationState != null && ours.Contains(t.destinationState.name)).ToArray())
                loco.RemoveTransition(t);
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && ours.Contains(t.destinationState.name)).ToArray())
                sm.RemoveAnyStateTransition(t);

            AnimatorState S(string name, string tag, Vector3 pos)
            {
                var s = State(sm, name) ?? sm.AddState(name, pos);
                ClearTransitions(s);
                s.motion = C(name);
                s.tag = tag;
                s.writeDefaultValues = true;
                return s;
            }

            // Death: from anywhere, holds the last frame until revived.
            for (int i = 0; i < 2; i++)
            {
                var death = S(i == 0 ? "Death A" : "Death B", "Action", new Vector3(-250f, 330f + i * 60f));
                var t = sm.AddAnyStateTransition(death);
                t.canTransitionToSelf = false;
                t.duration = 0.15f;
                t.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
                t.AddCondition(AnimatorConditionMode.Equals, i, "DeathIndex");
                Cond(death, loco, 0.5f, (AnimatorConditionMode.IfNot, 0f, "Dead"));
            }

            // Sitting.
            var sitEnter = S("SitEnter", "Action", new Vector3(-250f, 480f));
            var sitIdle = S("SitIdle", "Action", new Vector3(0f, 480f));
            var sitExit = S("SitExit", "Action", new Vector3(250f, 480f));
            Cond(loco, sitEnter, 0.2f, (AnimatorConditionMode.If, 0f, "Sitting"));
            Exit(sitEnter, sitIdle, 0.95f, 0.1f);
            Cond(sitEnter, sitExit, 0.2f, (AnimatorConditionMode.IfNot, 0f, "Sitting"));
            Cond(sitIdle, sitExit, 0.15f, (AnimatorConditionMode.IfNot, 0f, "Sitting"));
            Exit(sitExit, loco, 0.9f, 0.2f);

            // One-shots: start on Action == id, return on exit time (the next action may cut the blend short).
            var oneShots = new (string name, CharacterActions.ActionId id, float exit)[]
            {
                ("Interact", CharacterActions.ActionId.Interact, 0.9f),
                ("PickUp", CharacterActions.ActionId.PickUp, 0.9f),
                ("Jab", CharacterActions.ActionId.Jab, 0.75f),
                ("Cross", CharacterActions.ActionId.Cross, 0.75f),
                ("Repair", CharacterActions.ActionId.Repair, 0.93f),
            };
            for (int i = 0; i < oneShots.Length; i++)
            {
                var (name, id, exit) = oneShots[i];
                var s = S(name, "Action", new Vector3(-500f, -150f + i * 60f));
                Cond(loco, s, 0.12f, (AnimatorConditionMode.Equals, (int)id, "Action"));
                var back = Exit(s, loco, exit, 0.2f);
                back.interruptionSource = TransitionInterruptionSource.Destination;
            }

            // Looping emotes: stay while Action == id.
            var emotes = new (string name, CharacterActions.ActionId id)[]
            {
                ("Dance", CharacterActions.ActionId.Dance), ("Talk", CharacterActions.ActionId.Talk),
            };
            for (int i = 0; i < emotes.Length; i++)
            {
                var (name, id) = emotes[i];
                var s = S(name, "Emote", new Vector3(-500f, 180f + i * 60f));
                Cond(loco, s, 0.25f, (AnimatorConditionMode.Equals, (int)id, "Action"));
                Cond(s, loco, 0.25f, (AnimatorConditionMode.NotEqual, (int)id, "Action"));
            }

            // Pushing.
            var push = S("Push", "Push", new Vector3(-500f, 330f));
            Cond(loco, push, 0.2f, (AnimatorConditionMode.If, 0f, "Pushing"));
            Cond(push, loco, 0.25f, (AnimatorConditionMode.IfNot, 0f, "Pushing"));

            BuildHitLayer(controller, C("Hit Chest"), C("Hit Head"), UpperBodyMask(p));
            StrafeStatesBuilder.SyncExits(controller); // strafing must reach the new actions too

            EditorUtility.SetDirty(controller);
            return true;
        }

        /// <summary>Upper-body override layer for hit reactions when the Animator plays on its own.</summary>
        private static void BuildHitLayer(AnimatorController controller, AnimationClip chest, AnimationClip head, AvatarMask mask)
        {
            var layers = controller.layers;
            int index = System.Array.FindIndex(layers, l => l.name == HitLayerName);
            if (index < 0)
            {
                controller.AddLayer(HitLayerName);
                layers = controller.layers;
                index = layers.Length - 1;
            }

            var layer = layers[index];
            layer.avatarMask = mask;
            layer.defaultWeight = 1f;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.iKPass = true;
            var sm = layer.stateMachine;
            foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
            foreach (var t in sm.anyStateTransitions.ToArray()) sm.RemoveAnyStateTransition(t);

            var empty = sm.AddState("Empty", new Vector3(250f, 0f));
            sm.defaultState = empty;
            for (int i = 0; i < 2; i++)
            {
                var hit = sm.AddState(i == 0 ? "Hit Chest" : "Hit Head", new Vector3(250f, 100f + i * 60f));
                hit.motion = i == 0 ? chest : head;
                var t = sm.AddAnyStateTransition(hit);
                t.duration = 0.05f;
                t.canTransitionToSelf = true; // a new hit restarts the flinch
                t.AddCondition(AnimatorConditionMode.If, 0f, "Hit");
                t.AddCondition(AnimatorConditionMode.Equals, i, "HitIndex");
                Exit(hit, empty, 0.6f, 0.2f);
            }
            layers[index] = layer;
            controller.layers = layers;
        }

        /// <summary>Spine, chest, head and arms; legs and root keep the locomotion.</summary>
        public static AvatarMask UpperBodyMask(CharacterSetupProfile p)
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(p.UpperBodyMaskPath);
            if (mask == null)
            {
                EnsureFolderFor(p.UpperBodyMaskPath);
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, p.UpperBodyMaskPath);
            }
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                bool upper = part == AvatarMaskBodyPart.Body || part == AvatarMaskBodyPart.Head ||
                             part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm ||
                             part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers ||
                             part == AvatarMaskBodyPart.LeftHandIK || part == AvatarMaskBodyPart.RightHandIK;
                mask.SetHumanoidBodyPartActive(part, upper);
            }
            EditorUtility.SetDirty(mask);
            return mask;
        }

        public static void SetUpPlayer(GameObject root, CharacterSetupProfile p)
        {
            var actions = Kit.Ensure<CharacterActions>(root);
            actions.Controller = root.GetComponent<ThirdPersonController>();
            actions.Inputs = root.GetComponent<PlayerInputs>();
            actions.HitChestClip = p.HitChest;
            actions.HitHeadClip = p.HitHead;
            actions.UpperBodyMask = UpperBodyMask(p);
        }
    }
}
