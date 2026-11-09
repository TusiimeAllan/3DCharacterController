using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Adds the CharacterActions states to the Player Animator Controller, using the Universal Animation
    /// Library (CC0) plus one Mixamo death:
    ///   Any State -> Death A (UAL Death01) / Death B (Mixamo Dying Backwards), back to Locomotion on revive
    ///   Locomotion -> SitEnter -> SitIdle -> SitExit
    ///   Locomotion -> Interact, PickUp, Jab, Cross, Repair (one-shots, tag "Action")
    ///   Locomotion -> Dance, Talk (loops, tag "Emote"), Push (loop while pushing, tag "Push")
    ///   UpperBody layer: Hit Chest / Hit Head, used when motion matching is not hosting the controller.
    /// Edits the controller in place and is safe to re-run. Also adds CharacterActions to the Player prefab.
    /// </summary>
    public static class ActionStatesBuilder
    {
        private const string Tag = "[ActionStatesBuilder]";
        private const string UalPath = "Assets/Art/Animations/UAL/UAL1_Standard.fbx";
        private const string DyingBackwardsPath = "Assets/Art/Animations/XBot/X Bot@Dying Backwards.fbx";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string MaskPath = "Assets/Art/Animations/UpperBody.mask";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";
        private const string HitLayerName = "UpperBody Hits";

        [MenuItem("Tools/Character Controller/Add Actions (Hits, Death, Sit, Emotes, Push)")]
        public static void BuildDefault()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (!Apply(controller)) return;
            SetUpPlayer();
        }

        public static bool Apply(AnimatorController controller)
        {
            var clips = new (string state, AnimationClip clip)[]
            {
                ("Death A", Ual("Death01")), ("Death B", Clip(DyingBackwardsPath, "Dying Backwards")),
                ("SitEnter", Ual("Sitting_Enter")), ("SitIdle", Ual("Sitting_Idle_Loop")), ("SitExit", Ual("Sitting_Exit")),
                ("Interact", Ual("Interact")), ("PickUp", Ual("PickUp_Table")), ("Jab", Ual("Punch_Jab")),
                ("Cross", Ual("Punch_Cross")), ("Repair", Ual("Fixing_Kneeling")), ("Dance", Ual("Dance_Loop")),
                ("Talk", Ual("Idle_Talking_Loop")), ("Push", Ual("Push_Loop")),
                ("Hit Chest", Ual("Hit_Chest")), ("Hit Head", Ual("Hit_Head")),
            };
            var missing = clips.Where(c => c.clip == null).Select(c => c.state).ToArray();
            if (controller == null || missing.Length > 0)
            {
                Debug.LogError($"{Tag} Controller or clips missing ({string.Join(", ", missing)}), nothing changed.");
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

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null)
            {
                Debug.LogError($"{Tag} No Locomotion state.");
                return false;
            }

            // Remove what an earlier run added from Locomotion, then rebuild.
            string[] ours = { "Death A", "Death B", "SitEnter", "SitIdle", "SitExit", "Interact", "PickUp", "Jab",
                              "Cross", "Repair", "Dance", "Talk", "Push" };
            foreach (var t in loco.transitions.Where(t => t.destinationState != null && ours.Contains(t.destinationState.name)).ToArray())
                loco.RemoveTransition(t);
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && ours.Contains(t.destinationState.name)).ToArray())
                sm.RemoveAnyStateTransition(t);

            AnimatorState S(string name, string tag, Vector3 pos)
            {
                var s = sm.states.Select(x => x.state).FirstOrDefault(x => x.name == name) ?? sm.AddState(name, pos);
                foreach (var t in s.transitions.ToArray()) s.RemoveTransition(t);
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

            BuildHitLayer(controller, C("Hit Chest"), C("Hit Head"));

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Action states added to {AssetDatabase.GetAssetPath(controller)}.");
            return true;
        }

        /// <summary>Upper-body override layer for hit reactions when the Animator plays on its own.</summary>
        private static void BuildHitLayer(AnimatorController controller, AnimationClip chest, AnimationClip head)
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
            layer.avatarMask = UpperBodyMask();
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
        public static AvatarMask UpperBodyMask()
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask == null)
            {
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, MaskPath);
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

        /// <summary>Adds CharacterActions to the Player prefab with the hit clips and upper-body mask.</summary>
        public static void SetUpPlayer()
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var actions = root.GetComponent<CharacterActions>();
                if (actions == null) actions = root.AddComponent<CharacterActions>();
                actions.Controller = root.GetComponent<ThirdPersonController>();
                actions.Inputs = root.GetComponent<PlayerInputs>();
                actions.HitChestClip = Ual("Hit_Chest");
                actions.HitHeadClip = Ual("Hit_Head");
                actions.UpperBodyMask = UpperBodyMask();
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
                Debug.Log($"{Tag} CharacterActions set up on {PlayerPrefab}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void Param(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (controller.parameters.All(p => p.name != name)) controller.AddParameter(name, type);
        }

        private static AnimationClip Ual(string name) => Clip(UalPath, name);

        private static AnimationClip Clip(string path, string name) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == name || c.name.EndsWith("|" + name));

        private static AnimatorStateTransition Exit(AnimatorState from, AnimatorState to, float exitTime, float duration)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            t.duration = duration;
            return t;
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
