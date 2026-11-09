using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Auto-configures Mixamo FBX imports under Assets/Art:
    ///  - Humanoid rig (avatar created from each model)
    ///  - clip renamed from "mixamo.com" to the name after '@' in the file name
    ///  - loop enabled for cyclic locomotion clips
    /// Applied on first import only, so anything changed afterwards in the model's
    /// Import Settings Inspector is kept on reimport.
    /// </summary>
    public class MixamoModelPostprocessor : AssetPostprocessor
    {
        private const string ArtRoot = "/Art/";

        // Clips that should loop. Matched case-insensitively against the clip name.
        private static readonly string[] LoopingClips =
        {
            "Idle", "Walking", "Running", "Sprint", "Female Walk", "Crawling",
            "Swimming", "Floating", "Falling", "Crouching Idle", "Crouch Walk",
            "Crouching", "Hanging Idle", "Braced Hang Shimmy", "Climbing Ladder",
            "Hip Hop Dancing", "Punching Bag", "Talking", "Texting", "Sitting",
            "Strafe Walk Left", "Strafe Walk Right", "Strafe Walk Forward",
            "Strafe Run Left", "Strafe Run Right", "Strafe Run Forward"
        };

        private bool IsArtFbx =>
            assetPath.Replace('\\', '/').Contains(ArtRoot) &&
            assetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);

        private void OnPreprocessModel()
        {
            if (!IsArtFbx) return;

            var importer = (ModelImporter)assetImporter;
            if (!importer.importSettingsMissing) return;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.optimizeGameObjects = false;      // keep bones for sockets / IK
            importer.importNormals = ModelImporterNormals.Import;

            // Base models (@T-Pose or the bare model) don't carry animation. Files under an
            // Animations folder always do, even without '@' (e.g. the UAL animation library).
            bool isAnimationLibrary = assetPath.Replace('\\', '/').Contains("/Animations/");
            if (assetPath.Contains("@T-Pose") || (!assetPath.Contains("@") && !isAnimationLibrary))
                importer.importAnimation = false;
        }

        private void OnPreprocessAnimation()
        {
            if (!IsArtFbx) return;

            var importer = (ModelImporter)assetImporter;
            if (!importer.importSettingsMissing) return;
            var clips = importer.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;

            string niceName = DeriveClipName(assetPath);

            // LaFAN1 mocap for motion matching: root motion from the body (centre of mass and body
            // orientation, like the MxM demo mocap) so trajectories can be extracted; the FBX root node
            // exported from Blender never moves.
            if (assetPath.Replace('\\', '/').Contains("/LaFAN1/"))
            {
                foreach (var c in clips)
                {
                    if (clips.Length == 1) c.name = niceName;
                    c.loopTime = false;
                    c.keepOriginalOrientation = false;
                    c.keepOriginalPositionXZ = false;
                    c.keepOriginalPositionY = true;
                    c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = false;
                }
                importer.clipAnimations = clips;
                return;
            }

            for (int i = 0; i < clips.Length; i++)
            {
                if (clips.Length == 1) clips[i].name = niceName;
                clips[i].loopTime = ShouldLoop(clips[i].name) || ShouldLoop(niceName);
                clips[i].keepOriginalOrientation = true;
                clips[i].keepOriginalPositionXZ = true;
                clips[i].lockRootHeightY = true;
            }

            importer.clipAnimations = clips;
        }

        private static string DeriveClipName(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path); // e.g. "X Bot@Running Jump"
            int at = file.IndexOf('@');
            return at >= 0 ? file.Substring(at + 1).Trim() : file;
        }

        private static bool ShouldLoop(string clipName) =>
            LoopingClips.Any(l => clipName.Equals(l, System.StringComparison.OrdinalIgnoreCase));
    }
}
