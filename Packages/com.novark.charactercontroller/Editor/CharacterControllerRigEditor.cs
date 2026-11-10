using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// The Player's single Inspector: every part of the rig in one place, grouped into sections. The parts
    /// themselves stay hidden on the GameObject (toggle Show Components to see them).
    /// </summary>
    [CustomEditor(typeof(CharacterControllerRig))]
    public class CharacterControllerRigEditor : Editor
    {
        private class Section
        {
            public string Title;
            public Func<CharacterControllerRig, Object[]> Targets;
        }

        private static readonly Section[] Sections =
        {
            new Section { Title = "Characters", Targets = r => Of<CharacterSwitcher>(r) },
            new Section { Title = "Movement", Targets = r => Of<ThirdPersonController>(r) },
            new Section { Title = "Camera", Targets = r => r.Camera != null ? new Object[] { r.Camera } : Array.Empty<Object>() },
            new Section { Title = "Body Size", Targets = r => Of<CharacterDimensions>(r).Concat(Of<CharacterController>(r)).ToArray() },
            new Section { Title = "Parkour", Targets = r => Of<ParkourController>(r) },
            new Section { Title = "Ledges and Ladders", Targets = r => Of<ClimbController>(r) },
            new Section { Title = "Swimming", Targets = r => Of<SwimController>(r).Concat(Of<WaterEffects>(r)).Concat(Of<CharacterWetness>(r)).ToArray() },
            new Section { Title = "Actions", Targets = r => Of<CharacterActions>(r) },
            new Section { Title = "Grounding and Body IK", Targets = r => Of<HumanoidGrounding>(r).Concat(Of<BodyContactSolver>(r)).ToArray() },
            new Section { Title = "Motion Matching", Targets = r => r.GetComponents<MonoBehaviour>()
                .Where(m => m is LocomotionAnimationHost || (m != null && m.GetType().Namespace == "MxM")).Cast<Object>().ToArray() },
            new Section { Title = "Mounts and Sockets", Targets = r => Of<CharacterMountController>(r).Concat(Of<CharacterSockets>(r)).ToArray() },
            new Section { Title = "Input", Targets = r => Of<PlayerInputs>(r).Concat(r.GetComponents<Component>()
                .Where(c => c != null && c.GetType().Name == "PlayerInput")).ToArray() },
            new Section { Title = "Animator", Targets = r => Of<Animator>(r) },
        };

        private static readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
        private readonly Dictionary<Object, Editor> _editors = new Dictionary<Object, Editor>();

        private static Object[] Of<T>(CharacterControllerRig r) where T : Component =>
            r.GetComponents<T>().Cast<Object>().ToArray();

        private void OnDisable()
        {
            foreach (var e in _editors.Values) if (e != null) DestroyImmediate(e);
            _editors.Clear();
        }

        public override void OnInspectorGUI()
        {
            var rig = (CharacterControllerRig)target;
            serializedObject.Update();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Character Wizard")) CharacterWizard.Open(null);
                if (GUILayout.Button("Validate Scene")) SceneValidator.ValidateAndReport();
                if (!EditorUtility.IsPersistent(rig) && GUILayout.Button("Rebuild From Profile")) Rebuild(rig);
            }
            var show = serializedObject.FindProperty(nameof(CharacterControllerRig.ShowComponents));
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(show);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                CharacterControllerRig.HideParts(rig, !rig.ShowComponents);
                EditorUtility.SetDirty(rig);
            }
            if (Application.isPlaying && rig.Body != null)
                EditorGUILayout.LabelField("Body scale", $"{rig.Body.Scale:F2} x the reference body (values below are scaled)");
            EditorGUILayout.Space(4);

            foreach (var section in Sections)
            {
                var targets = section.Targets(rig).Where(t => t != null).ToArray();
                if (targets.Length == 0) continue;
                Open.TryGetValue(section.Title, out bool open);
                open = EditorGUILayout.BeginFoldoutHeaderGroup(open, section.Title);
                Open[section.Title] = open;
                if (open)
                {
                    EditorGUI.indentLevel++;
                    foreach (var t in targets)
                    {
                        if (targets.Length > 1) EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(t.GetType().Name), EditorStyles.miniBoldLabel);
                        if (!_editors.TryGetValue(t, out var editor) || editor == null)
                            _editors[t] = editor = CreateEditor(t);
                        editor.OnInspectorGUI();
                        EditorGUILayout.Space(4);
                    }
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>Re-applies the kit profile to this Player in place (components, clips, controller), keeping its characters.</summary>
        private static void Rebuild(CharacterControllerRig rig)
        {
            var p = CharacterSetupProfileEditor.FindOrCreateProfile();
            var controller = CharacterSetupBuilder.BuildAnimator(p);
            Kit.UseUndo = true;
            try { CharacterSetupBuilder.ConfigurePlayer(rig.gameObject, p, controller); }
            finally { Kit.UseUndo = false; }
            CharacterControllerRig.HideParts(rig, !rig.ShowComponents);
        }
    }
}
