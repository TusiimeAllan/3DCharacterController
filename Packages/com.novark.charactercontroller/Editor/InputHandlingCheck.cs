using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// The character reads its controls through the Input System. In a project that only has the old Input
    /// Manager active, this offers (once) to switch Active Input Handling to Both, which keeps the old input
    /// working, and restarts Unity so it takes effect.
    /// </summary>
    [InitializeOnLoad]
    public static class InputHandlingCheck
    {
        private const string Declined = "CharacterKit.InputHandlingDeclined.";

        static InputHandlingCheck()
        {
#if !ENABLE_INPUT_SYSTEM
            if (Application.isBatchMode || SessionState.GetBool("CharacterKit.InputChecked", false)) return;
            SessionState.SetBool("CharacterKit.InputChecked", true);
            if (EditorPrefs.GetBool(Declined + Application.dataPath, false)) return;
            EditorApplication.delayCall += Ask;
#endif
        }

        [MenuItem("Tools/Character Controller/Enable Input System (Both)", priority = 41)]
        public static void EnableFromMenu()
        {
            if (Enabled())
            {
                EditorUtility.DisplayDialog("Input System", "The Input System is already active in this project.", "OK");
                return;
            }
            Switch();
        }

        public static bool Enabled()
        {
#if ENABLE_INPUT_SYSTEM
            return true;
#else
            return false;
#endif
        }

        private static void Ask()
        {
            int choice = EditorUtility.DisplayDialogComplex("Character Controller",
                "The character reads keyboard, mouse and gamepad through the Input System, which is turned off in this project " +
                "(Active Input Handling: Input Manager).\n\nSwitch it to Both? Your existing Input Manager setup keeps working. " +
                "Unity restarts to apply it.",
                "Switch and Restart", "Not Now", "Don't Ask Again");
            if (choice == 0) Switch();
            else if (choice == 2) EditorPrefs.SetBool(Declined + Application.dataPath, true);
        }

        private static void Switch()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settings == null || settings.Length == 0) return;
            var so = new SerializedObject(settings[0]);
            var prop = so.FindProperty("activeInputHandler");
            if (prop == null) return;
            prop.intValue = 2; // 0 = Input Manager, 1 = Input System, 2 = Both
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            if (EditorUtility.DisplayDialog("Character Controller", "Active Input Handling is now Both. Restart Unity now?", "Restart", "Later"))
                EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }
    }
}
