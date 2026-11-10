using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    [CustomEditor(typeof(CharacterSwitcher))]
    public class CharacterSwitcherEditor : Editor
    {
        private GameObject _modelToAdd;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var switcher = (CharacterSwitcher)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Active Character", EditorStyles.boldLabel);

            if (switcher.Characters.Count == 0)
            {
                EditorGUILayout.HelpBox("No characters yet. Drag a Humanoid model below to add one.", MessageType.Info);
            }
            else
            {
                var names = switcher.Characters
                    .Select((c, i) => string.IsNullOrEmpty(c?.Name) ? $"Character {i + 1}" : c.Name)
                    .ToArray();
                int picked = GUILayout.SelectionGrid(switcher.ActiveIndex, names, Mathf.Min(names.Length, 3));
                if (picked != switcher.ActiveIndex) Switch(switcher, picked);

                if (Application.isPlaying)
                    EditorGUILayout.HelpBox("Play Mode: switching keeps the current animation frame.", MessageType.None);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Add Character", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                _modelToAdd = (GameObject)EditorGUILayout.ObjectField("Humanoid Model", _modelToAdd, typeof(GameObject), false);
                using (new EditorGUI.DisabledScope(_modelToAdd == null))
                {
                    if (GUILayout.Button("Add As Child")) AddCharacter(switcher, _modelToAdd);
                }
            }
        }

        private static void Switch(CharacterSwitcher switcher, int index)
        {
            if (Application.isPlaying)
            {
                switcher.SwitchTo(index);
                return;
            }

            var animator = switcher.GetComponent<Animator>();
            var undoTargets = switcher.Characters.Where(c => c?.Root != null)
                .Select(c => (Object)c.Root)
                .Append(switcher)
                .Append(animator)
                .ToArray();
            Undo.RecordObjects(undoTargets, "Switch Character");

            switcher.SwitchTo(index);

            foreach (var o in undoTargets)
            {
                EditorUtility.SetDirty(o);
                PrefabUtility.RecordPrefabInstancePropertyModifications(o);
            }
            if (switcher.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(switcher.gameObject.scene);
        }

        private static void AddCharacter(CharacterSwitcher switcher, GameObject model)
        {
            Undo.RecordObject(switcher, "Add Character");
            var entry = CharacterSetupBuilder.CreateCharacterChild(switcher.transform, model, model.name, true);
            entry.Root.SetActive(false);
            switcher.Characters.Add(entry);
            EditorUtility.SetDirty(switcher);
            PrefabUtility.RecordPrefabInstancePropertyModifications(switcher);
            if (switcher.Characters.Count == 1) Switch(switcher, 0);
        }
    }
}
