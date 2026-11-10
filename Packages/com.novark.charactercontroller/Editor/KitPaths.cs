using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Where the kit's bundled source assets live inside the package, and helpers to load clips from them.
    /// Generated assets (Animator Controller, prefab, parkour library) are written to the project instead,
    /// see <see cref="CharacterSetupProfile.OutputFolder"/>.
    /// </summary>
    public static class KitPaths
    {
        public const string PackageName = "com.novark.charactercontroller";
        public const string Root = "Packages/" + PackageName;

        public const string Ual = Root + "/Animations/UAL/UAL1_Standard.fbx";
        public const string Mixamo = Root + "/Animations/Mixamo/X Bot@";
        public const string XBot = Root + "/Characters/XBot/X Bot.fbx";
        public const string InputActions = Root + "/Input/PlayerControls.inputactions";

        public const string MxmDemo = Root + "/ThirdParty/MotionMatching/Demo";
        public const string MxmMocap = MxmDemo + "/Animations/Mocap/";
        public const string MxmEvents = MxmDemo + "/Animations/Events/";
        public const string MxmDemoCharacter = MxmDemo + "/Prefabs/Robot Kyle_MOCAP_Balanced.prefab";
        public const string MxmAnimData = MxmDemo + "/Data/MotionMatching/MocapAnimData.asset";
        public const string MxmProfiles = MxmDemo + "/Data/InputProfiles/Balanced/";

        /// <summary>A clip inside a multi-clip FBX by name (Blender exports prefix it with "Armature|").</summary>
        public static AnimationClip ClipIn(string fbxPath, string clipName) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == clipName || c.name.EndsWith("|" + clipName));

        /// <summary>The single clip of a one-clip FBX (Mixamo downloads).</summary>
        public static AnimationClip FirstClip(string fbxPath) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

        public static AnimationClip UalClip(string name) => ClipIn(Ual, name);
        public static AnimationClip MixamoClip(string name) => FirstClip(Mixamo + name + ".fbx");
        public static AnimationClip MocapClip(string fbx, string clipName) => ClipIn(MxmMocap + fbx, clipName);
        public static AnimationClip EventClip(string relativePath) =>
            AssetDatabase.LoadAssetAtPath<AnimationClip>(MxmEvents + relativePath);
    }
}
