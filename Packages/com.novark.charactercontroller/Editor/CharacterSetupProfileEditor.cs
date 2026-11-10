using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    [CustomEditor(typeof(CharacterSetupProfile))]
    public class CharacterSetupProfileEditor : Editor
    {
        public const string DefaultProfilePath = "Assets/CharacterKit/CharacterKitProfile.asset";

        public override void OnInspectorGUI()
        {
            var profile = (CharacterSetupProfile)target;
            if (GUILayout.Button("Open Character Wizard", GUILayout.Height(28))) CharacterWizard.Open(profile);
            EditorGUILayout.Space(6);

            DrawDefaultInspector();
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Kit Clips", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill Empty With Kit Clips"))
                {
                    Undo.RecordObject(profile, "Fill Kit Defaults");
                    CharacterSetupBuilder.ApplyKitDefaults(profile, overwrite: false);
                }
                if (GUILayout.Button("Reset All To Kit Clips") &&
                    EditorUtility.DisplayDialog("Reset clips", "Replace every clip in this profile with the bundled kit clips?", "Reset", "Cancel"))
                {
                    Undo.RecordObject(profile, "Reset Kit Defaults");
                    CharacterSetupBuilder.ApplyKitDefaults(profile, overwrite: true);
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);
            if (GUILayout.Button("1. Build Animator Controller")) CharacterSetupBuilder.BuildAnimator(profile);
            if (GUILayout.Button("2. Build Player Prefab")) CharacterSetupBuilder.BuildPlayerPrefab(profile);
            if (GUILayout.Button("3. Add Player To Open Scene")) CharacterSetupBuilder.AddPlayerToOpenScene(profile);
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Build Everything", GUILayout.Height(28))) BuildEverything(profile, addToScene: true);
        }

        public static void BuildEverything(CharacterSetupProfile profile, bool addToScene)
        {
            CharacterSetupBuilder.BuildAnimator(profile);
            CharacterSetupBuilder.BuildPlayerPrefab(profile);
            if (addToScene) CharacterSetupBuilder.AddPlayerToOpenScene(profile);
        }

        /// <summary>The project's kit profile: the first one found, or a new one filled with the kit clips.</summary>
        public static CharacterSetupProfile FindOrCreateProfile()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(CharacterSetupProfile));
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<CharacterSetupProfile>(AssetDatabase.GUIDToAssetPath(guids[0]));
            var profile = CreateInstance<CharacterSetupProfile>();
            CharacterSetupBuilder.ApplyKitDefaults(profile, overwrite: true);
            AnimatorBuildUtil.EnsureFolderFor(DefaultProfilePath);
            AssetDatabase.CreateAsset(profile, DefaultProfilePath);
            AssetDatabase.SaveAssets();
            return profile;
        }

        [MenuItem("Tools/Character Controller/Select Kit Profile", priority = 20)]
        public static void SelectProfile()
        {
            var profile = FindOrCreateProfile();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        [MenuItem("Tools/Character Controller/Rebuild Player From Profile", priority = 21)]
        public static void Rebuild() => BuildEverything(FindOrCreateProfile(), addToScene: false);
    }
}
