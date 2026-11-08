using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    [CustomEditor(typeof(CharacterSetupProfile))]
    public class CharacterSetupProfileEditor : Editor
    {
        private const string DefaultProfilePath = "Assets/Settings/CharacterSetupProfile.asset";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var profile = (CharacterSetupProfile)target;

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);

            if (GUILayout.Button("Fill With Project Defaults"))
            {
                Undo.RecordObject(profile, "Fill Profile Defaults");
                CharacterSetupBuilder.ApplyProjectDefaults(profile);
                EditorUtility.SetDirty(profile);
            }

            if (GUILayout.Button("1. Build Animator Controller")) CharacterSetupBuilder.BuildAnimator(profile);
            if (GUILayout.Button("2. Build Player Prefab")) CharacterSetupBuilder.BuildPlayerPrefab(profile);
            if (GUILayout.Button("3. Add Player To Open Scene")) CharacterSetupBuilder.AddPlayerToOpenScene(profile);

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Build Everything", GUILayout.Height(28)))
            {
                CharacterSetupBuilder.BuildAnimator(profile);
                CharacterSetupBuilder.BuildPlayerPrefab(profile);
                CharacterSetupBuilder.AddPlayerToOpenScene(profile);
            }
        }

        /// <summary>Selects (or creates) the project's setup profile so it opens in the Inspector.</summary>
        [MenuItem("Tools/Character Controller/Select Setup Profile")]
        public static void SelectProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CharacterSetupProfile>(DefaultProfilePath);
            if (profile == null)
            {
                var guids = AssetDatabase.FindAssets("t:" + nameof(CharacterSetupProfile));
                if (guids.Length > 0)
                    profile = AssetDatabase.LoadAssetAtPath<CharacterSetupProfile>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            if (profile == null)
            {
                profile = CreateInstance<CharacterSetupProfile>();
                CharacterSetupBuilder.ApplyProjectDefaults(profile);
                AssetDatabase.CreateAsset(profile, DefaultProfilePath);
                AssetDatabase.SaveAssets();
            }
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }
    }
}
