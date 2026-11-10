using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Ledge hanging and ladder climbing (ClimbController):
    /// - imports the Mixamo Hanging Idle, Braced Hang Shimmy and Climbing Ladder clips looping, with the body
    ///   (not the recorded root) driving orientation and position;
    /// - adds base-layer states entered from Any State: Hang Idle / Hang Shimmy R / Hang Shimmy L on the
    ///   Hanging bool and HangMove float, Ladder on OnLadder (playback speed from LadderSpeed);
    /// - adds ClimbController to the Player prefab.
    /// Safe to re-run.
    /// </summary>
    public static class ClimbStatesBuilder
    {
        private const string Tag = "[ClimbStatesBuilder]";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";
        private const string Mixamo = "Assets/Art/Animations/XBot/X Bot@";
        private static readonly string[] States = { "Hang Idle", "Hang Shimmy R", "Hang Shimmy L", "Ladder" };

        [MenuItem("Tools/Character Controller/Add Ledges and Ladders")]
        public static void BuildAll()
        {
            Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            SetUpPlayer();
        }

        public static void Apply(AnimatorController controller)
        {
            var hang = Clip("Hanging Idle");
            var shimmy = Clip("Braced Hang Shimmy");
            var ladder = Clip("Climbing Ladder");
            if (controller == null || hang == null || shimmy == null || ladder == null)
            {
                Debug.LogError($"{Tag} Controller or Mixamo climb clips missing.");
                return;
            }

            foreach (var (name, type) in new[]
                     {
                         ("Hanging", AnimatorControllerParameterType.Bool), ("HangMove", AnimatorControllerParameterType.Float),
                         ("OnLadder", AnimatorControllerParameterType.Bool), ("LadderSpeed", AnimatorControllerParameterType.Float),
                     })
                if (controller.parameters.All(p => p.name != name)) controller.AddParameter(name, type);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            var fall = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Fall") ?? loco;
            if (loco == null) return;

            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null && States.Contains(t.destinationState.name)).ToArray())
                sm.RemoveAnyStateTransition(t);
            AnimatorState S(string name, Motion motion, Vector3 pos)
            {
                var s = sm.states.Select(x => x.state).FirstOrDefault(x => x.name == name) ?? sm.AddState(name, pos);
                foreach (var t in s.transitions.ToArray()) s.RemoveTransition(t);
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

            foreach (var s in new[] { idle, right, left })
            {
                var t = s.AddTransition(fall); // let go: fall (climbing up is a parkour state, entered from Any State)
                t.hasExitTime = false;
                t.duration = 0.2f;
                t.AddCondition(AnimatorConditionMode.IfNot, 0f, "Hanging");
            }
            var off = climb.AddTransition(loco);
            off.hasExitTime = false;
            off.duration = 0.2f;
            off.AddCondition(AnimatorConditionMode.IfNot, 0f, "OnLadder");

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Hang and ladder states added.");
        }

        /// <summary>Loads a Mixamo climb clip, making sure it loops and follows the body (not the recorded root).</summary>
        private static AnimationClip Clip(string name)
        {
            string path = Mixamo + name + ".fbx";
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return null;
            var defs = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            bool changed = false;
            foreach (var d in defs)
            {
                if (d.loopTime && !d.keepOriginalOrientation && !d.keepOriginalPositionXZ) continue;
                d.loopTime = true;
                d.keepOriginalOrientation = false;
                d.keepOriginalPositionXZ = false;
                d.lockRootRotation = true;
                d.lockRootPositionXZ = true;
                changed = true;
            }
            if (changed)
            {
                importer.clipAnimations = defs;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        }

        public static void SetUpPlayer()
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var climb = root.GetComponent<ClimbController>() ?? root.AddComponent<ClimbController>();
                climb.Controller = root.GetComponent<ThirdPersonController>();
                climb.Parkour = root.GetComponent<ParkourController>();
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
                Debug.Log($"{Tag} ClimbController on {PlayerPrefab}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
