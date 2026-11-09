using System.Collections.Generic;
using System.Linq;
using MxM;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// One-click setup for MxM motion matching on the Player, plus demo preparation.
    /// The Player keeps ThirdPersonController in charge of movement (MxM root motion is off).
    /// </summary>
    public static class MotionMatchingSetup
    {
        private const string Tag = "[MotionMatchingSetup]";
        private const string Menu = "Tools/Character Controller/Motion Matching/";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";
        private const string Demo = "Assets/Plugins/MotionMatching/Demo";
        private const string DemoCharacter = Demo + "/Prefabs/Robot Kyle_MOCAP_Balanced.prefab";
        private const string AnimData = Demo + "/Data/MotionMatching/MocapAnimData.asset";
        private const string WalkProfile = Demo + "/Data/InputProfiles/Balanced/MocapInputProfile_Balanced.asset";
        private const string SprintProfile = Demo + "/Data/InputProfiles/Balanced/MocapSprintInputProfile_Balanced.asset";
        private const string Mocap = Demo + "/Animations/Mocap/";
        private const string ProfilePath = "Assets/Settings/CharacterSetupProfile.asset";

        /// <summary>Our Mixamo locomotion clips that the MxM mocap library replaces.</summary>
        public static readonly string[] ReplacedClips =
        {
            "Assets/Art/Animations/XBot/X Bot@Idle.fbx",
            "Assets/Art/Animations/XBot/X Bot@Walking.fbx",
            "Assets/Art/Animations/XBot/X Bot@Running.fbx",
            "Assets/Art/Animations/XBot/X Bot@Sprint.fbx",
            "Assets/Art/Animations/XBot/X Bot@Female Walk.fbx",
            "Assets/Art/Animations/ManInSuit/Man In Suit@Idle.fbx",
            "Assets/Art/Animations/ManInSuit/Man In Suit@Running.fbx",
            "Assets/Art/Animations/ManInSuit/Man In Suit@Walking.fbx",
        };

        // ================================================================ Demo preparation

        [MenuItem(Menu + "Prepare Demo (URP materials + input)")]
        public static void PrepareDemo()
        {
            EnableBothInputHandlers();
            int converted = ConvertDemoMaterials();
            Debug.Log($"{Tag} Demo prepared: {converted} materials converted to URP Lit. " +
                      "Restart the Editor so the input handling change takes effect.");
        }

        /// <summary>The MxM demo reads the legacy Input class, so allow both input backends.</summary>
        public static void EnableBothInputHandlers()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings == null) return;
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("activeInputHandler");
            if (prop == null || prop.intValue == 2) return;
            prop.intValue = 2; // 0 = old, 1 = new, 2 = both
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        public static int ConvertDemoMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Plugins/MotionMatching" }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (mat == null || mat.shader == lit || mat.shader.name.StartsWith("Universal Render Pipeline")) continue;

                Texture main = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                Vector2 scale = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex") : Vector2.one;
                Vector2 offset = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
                Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                float gloss = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.3f;
                float metal = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
                Texture normal = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
                Color emission = mat.HasProperty("_EmissionColor") && mat.IsKeywordEnabled("_EMISSION")
                    ? mat.GetColor("_EmissionColor") : Color.black;

                mat.shader = lit;
                mat.SetTexture("_BaseMap", main);
                mat.SetTextureScale("_BaseMap", scale);
                mat.SetTextureOffset("_BaseMap", offset);
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Smoothness", gloss);
                mat.SetFloat("_Metallic", metal);
                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                }
                if (emission.maxColorComponent > 0f)
                {
                    mat.SetColor("_EmissionColor", emission);
                    mat.EnableKeyword("_EMISSION");
                }
                EditorUtility.SetDirty(mat);
                count++;
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        // ================================================================ Clip replacement

        [MenuItem(Menu + "Use MxM Clips For Locomotion (delete replaced)")]
        public static void ReplaceLocomotionClips()
        {
            var idle = MocapClip("WalkingMocapSet.fbx", "01_IdleLoop");
            var walk = MocapClip("WalkingMocapSet.fbx", "24_WalkStraight_Neutral_01");
            var run = MocapClip("RunningMocapSet.fbx", "24_RunCycle_Neutral_01");
            var sprint = MocapClip("SprintMocapSet.fbx", "SprintLoop");
            if (idle == null || walk == null || run == null || sprint == null)
            {
                Debug.LogError($"{Tag} Could not find the MxM mocap clips, nothing was changed.");
                return;
            }

            // Profile (used whenever the Animator Controller is rebuilt).
            var profile = AssetDatabase.LoadAssetAtPath<CharacterSetupProfile>(ProfilePath);
            if (profile != null)
            {
                profile.Idle = idle;
                profile.Walk = walk;
                profile.Run = run;
                profile.Sprint = sprint;
                EditorUtility.SetDirty(profile);

                // The existing controller, edited in place so every reference to it stays valid.
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(profile.ControllerPath);
                var loco = controller != null
                    ? controller.layers[0].stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion")
                    : null;
                if (loco != null && loco.motion is BlendTree tree)
                {
                    var replacement = new Motion[] { idle, walk, run, sprint };
                    var children = tree.children;
                    for (int i = 0; i < children.Length && i < replacement.Length; i++) children[i].motion = replacement[i];
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                    EditorUtility.SetDirty(controller);
                }
            }

            // Delete only what nothing references any more.
            var toDelete = ReplacedClips.Where(p => AssetDatabase.LoadMainAssetAtPath(p) != null).ToList();
            AssetDatabase.SaveAssets(); // dependency lookup reads the files on disk
            var stillUsed = new List<string>();
            foreach (var dep in AssetDatabase.FindAssets("t:AnimatorController t:Prefab t:Scene t:CharacterSetupProfile")
                         .Select(AssetDatabase.GUIDToAssetPath))
            {
                foreach (var d in AssetDatabase.GetDependencies(dep, false))
                    if (toDelete.Contains(d) && !stillUsed.Contains(d)) stillUsed.Add($"{d} (used by {dep})");
            }
            if (stillUsed.Count > 0)
            {
                Debug.LogError($"{Tag} Not deleting, still referenced:\n" + string.Join("\n", stillUsed));
                return;
            }

            var failed = new List<string>();
            AssetDatabase.DeleteAssets(toDelete.ToArray(), failed);
            if (AssetDatabase.IsValidFolder("Assets/Art/Animations/ManInSuit") &&
                AssetDatabase.FindAssets("", new[] { "Assets/Art/Animations/ManInSuit" }).Length == 0)
                AssetDatabase.DeleteAsset("Assets/Art/Animations/ManInSuit");
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Locomotion now uses MxM mocap clips. Deleted {toDelete.Count - failed.Count} replaced clips.");
        }

        public static AnimationClip MocapClip(string fbx, string clipName) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(Mocap + fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == clipName);

        // ================================================================ Player

        [MenuItem(Menu + "Set Up Player")]
        public static void SetUpPlayer()
        {
            var demo = AssetDatabase.LoadAssetAtPath<GameObject>(DemoCharacter);
            var animData = AssetDatabase.LoadAssetAtPath<MxMAnimData>(AnimData);
            if (demo == null || animData == null)
            {
                Debug.LogError($"{Tag} MxM demo data not found under {Demo}.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var animator = root.GetComponent<Animator>();
                animator.applyRootMotion = false; // the CharacterController moves the character

                // Start from the demo's tuned settings, then change only what our controller needs.
                var mxm = root.GetComponent<MxMAnimator>();
                if (mxm == null) mxm = root.AddComponent<MxMAnimator>();
                EditorUtility.CopySerialized(demo.GetComponentInChildren<MxMAnimator>(true), mxm);
                var so = new SerializedObject(mxm);
                var data = so.FindProperty("m_animData");
                data.arraySize = 1;
                data.GetArrayElementAtIndex(0).objectReferenceValue = animData;
                so.FindProperty("m_rootMotionMode").intValue = (int)EMxMRootMotion.Off;
                so.FindProperty("m_animControllerLayer").intValue = 1;
                so.FindProperty("m_autoCreateAnimatorController").boolValue = false;
                ClearForeignReferences(so, root);
                so.ApplyModifiedPropertiesWithoutUndo();

                var traj = root.GetComponent<MxMTrajectoryGenerator>();
                if (traj == null) traj = root.AddComponent<MxMTrajectoryGenerator>();
                EditorUtility.CopySerialized(demo.GetComponentInChildren<MxMTrajectoryGenerator>(true), traj);
                var st = new SerializedObject(traj);
                st.FindProperty("m_customInput").boolValue = true; // fed by MotionMatchingLocomotion
                st.FindProperty("m_camTransform").objectReferenceValue = null; // set at runtime from the controller
                st.FindProperty("m_mxmInputProfile").objectReferenceValue = AssetDatabase.LoadAssetAtPath<MxMInputProfile>(WalkProfile);
                ClearForeignReferences(st, root);
                st.ApplyModifiedPropertiesWithoutUndo();

                var bridge = root.GetComponent<MotionMatchingLocomotion>();
                if (bridge == null) bridge = root.AddComponent<MotionMatchingLocomotion>();
                bridge.Controller = root.GetComponent<ThirdPersonController>();
                bridge.Inputs = root.GetComponent<PlayerInputs>();
                bridge.Mxm = mxm;
                bridge.Trajectory = traj;
                bridge.WalkProfile = AssetDatabase.LoadAssetAtPath<MxMInputProfile>(WalkProfile);
                bridge.SprintProfile = AssetDatabase.LoadAssetAtPath<MxMInputProfile>(SprintProfile);

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
                Debug.Log($"{Tag} Player prefab set up for motion matching.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Nulls references copied from the demo prefab that point at objects outside our hierarchy.</summary>
        private static void ClearForeignReferences(SerializedObject so, GameObject root)
        {
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                var go = it.objectReferenceValue is Component c ? c.gameObject : it.objectReferenceValue as GameObject;
                if (go != null && !go.transform.IsChildOf(root.transform)) it.objectReferenceValue = null;
            }
        }
    }
}
