using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Adds a Strafe state to the Player Animator Controller: a 2D freeform directional blend tree on
    /// StrafeX / StrafeZ (velocity in the character's frame) with idle, walk and run in four directions
    /// (Mixamo Locomotion Pack; backwards is the forward cycle played in reverse).
    ///   Locomotion <-> Strafe on the Strafing bool.
    /// Strafe gets a copy of every other exit Locomotion has (jump, fall, crouch, actions, emotes, push),
    /// so strafing never blocks them. Re-run after anything adds exits to Locomotion. Safe to re-run.
    /// Motion matching hands strafing to this state (its mocap has no sideways or backwards clips).
    /// </summary>
    public static class StrafeStatesBuilder
    {
        private const string Tag = "[StrafeStatesBuilder]";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string ClipPrefix = "Assets/Art/Animations/XBot/X Bot@Strafe ";
        public const string StateName = "Strafe";
        private const float EnterDuration = 0.2f;
        private const float ExitDuration = 0.25f;

        private static readonly string[] ClipNames =
            { "Walk Forward", "Walk Left", "Walk Right", "Run Forward", "Run Left", "Run Right" };

        [MenuItem("Tools/Character Controller/Add Strafe Mode")]
        public static void BuildDefault() =>
            Apply(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));

        public static bool Apply(AnimatorController controller)
        {
            var clips = ClipNames.ToDictionary(n => n, LoadLooping);
            var missing = clips.Where(c => c.Value == null).Select(c => c.Key).ToArray();
            if (controller == null || missing.Length > 0)
            {
                Debug.LogError($"{Tag} Controller or clips missing ({string.Join(", ", missing)}), nothing changed.");
                return false;
            }

            Param(controller, "Strafing", AnimatorControllerParameterType.Bool);
            Param(controller, "StrafeX", AnimatorControllerParameterType.Float);
            Param(controller, "StrafeZ", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            var loco = State(sm, "Locomotion");
            if (loco == null)
            {
                Debug.LogError($"{Tag} No Locomotion state.");
                return false;
            }

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
            foreach (var gait in new[] { "Walk", "Run" })
            {
                var fwd = clips[gait + " Forward"];
                float fwdSpeed = Mathf.Abs(fwd.averageSpeed.z);
                tree.AddChild(fwd, new Vector2(0f, fwdSpeed));
                tree.AddChild(fwd, new Vector2(0f, -fwdSpeed)); // reversed below: backwards
                tree.AddChild(clips[gait + " Left"], new Vector2(-Mathf.Abs(clips[gait + " Left"].averageSpeed.x), 0f));
                tree.AddChild(clips[gait + " Right"], new Vector2(Mathf.Abs(clips[gait + " Right"].averageSpeed.x), 0f));
            }
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
                if (children[i].position.y < -0.01f) children[i].timeScale = -1f;
            tree.children = children;

            strafe.motion = tree;
            strafe.writeDefaultValues = true;
            SyncExits(controller);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Strafe state added to {AssetDatabase.GetAssetPath(controller)}.");
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
            foreach (var t in strafe.transitions.ToArray()) strafe.RemoveTransition(t);

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

        /// <summary>Loads a strafe clip and makes sure it is imported looping.</summary>
        private static AnimationClip LoadLooping(string name)
        {
            string path = ClipPrefix + name + ".fbx";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) return null;
            var defs = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            if (defs.Any(d => !d.loopTime))
            {
                foreach (var d in defs)
                {
                    d.loopTime = true;
                    d.name = "Strafe " + name;
                }
                importer.clipAnimations = defs;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        }

        private static AnimatorState State(AnimatorStateMachine sm, string name) =>
            sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name);

        private static void Param(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (controller.parameters.All(p => p.name != name)) controller.AddParameter(name, type);
        }
    }
}
