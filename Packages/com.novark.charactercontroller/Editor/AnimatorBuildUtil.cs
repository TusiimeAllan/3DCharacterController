using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>Shared helpers for the feature builders.</summary>
    public static class AnimatorBuildUtil
    {
        public static AnimatorState State(AnimatorStateMachine sm, string name) =>
            sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name);

        public static AnimatorState State(AnimatorController controller, string name) =>
            controller != null && controller.layers.Length > 0 ? State(controller.layers[0].stateMachine, name) : null;

        public static void Param(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (controller.parameters.All(p => p.name != name)) controller.AddParameter(name, type);
        }

        public static void ClearTransitions(AnimatorState s)
        {
            foreach (var t in s.transitions.ToArray()) s.RemoveTransition(t);
        }

        public static AnimatorStateTransition Exit(AnimatorState from, AnimatorState to, float exitTime, float duration)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            t.duration = duration;
            return t;
        }

        public static AnimatorStateTransition Cond(AnimatorState from, AnimatorState to, float duration,
            params (AnimatorConditionMode mode, float threshold, string param)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            foreach (var (mode, threshold, param) in conditions) t.AddCondition(mode, threshold, param);
            return t;
        }

        /// <summary>
        /// Names of the profile fields that are empty, for "skipped, missing X" warnings.
        /// Returns an empty array when every clip is set.
        /// </summary>
        public static string[] Missing(params (string field, Object value)[] fields) =>
            fields.Where(f => f.value == null).Select(f => f.field).ToArray();

        public static void Skip(string tag, string feature, IEnumerable<string> missing) =>
            Debug.LogWarning($"{tag} {feature} skipped, the profile has no clip for: {string.Join(", ", missing)}.");

        /// <summary>
        /// Makes sure a clip imported from a model file loops (and, optionally, follows the body rather than
        /// the recorded root, which ledge and ladder clips need). Read-only package clips are left as they are.
        /// </summary>
        public static void EnsureImport(AnimationClip clip, bool loop, bool bodyDriven, string renameTo = null)
        {
            if (clip == null) return;
            string path = AssetDatabase.GetAssetPath(clip);
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return;
            var defs = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            bool changed = false;
            foreach (var d in defs)
            {
                if (d.name != clip.name && defs.Length > 1) continue;
                if (loop && !d.loopTime) { d.loopTime = true; changed = true; }
                if (bodyDriven && (d.keepOriginalOrientation || d.keepOriginalPositionXZ || !d.lockRootRotation || !d.lockRootPositionXZ))
                {
                    d.keepOriginalOrientation = false;
                    d.keepOriginalPositionXZ = false;
                    d.lockRootRotation = true;
                    d.lockRootPositionXZ = true;
                    changed = true;
                }
                if (renameTo != null && defs.Length == 1 && d.name != renameTo) { d.name = renameTo; changed = true; }
            }
            if (!changed) return;
            if (path.StartsWith("Packages/") && !IsWritable(path))
            {
                Debug.LogWarning($"[CharacterKit] {path} is read-only; set its import options (loop{(bodyDriven ? ", body orientation" : "")}) by hand.");
                return;
            }
            importer.clipAnimations = defs;
            importer.SaveAndReimport();
        }

        private static bool IsWritable(string path)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            return info == null || info.source == UnityEditor.PackageManager.PackageSource.Embedded ||
                   info.source == UnityEditor.PackageManager.PackageSource.Local;
        }

        /// <summary>Reloads a clip after a reimport may have replaced the object.</summary>
        public static AnimationClip Reload(AnimationClip clip)
        {
            if (clip == null) return null;
            string path = AssetDatabase.GetAssetPath(clip);
            string name = clip.name;
            var all = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>().ToList();
            return all.FirstOrDefault(c => c.name == name) ??
                   (all.Count > 0 ? all.FirstOrDefault(c => !c.name.StartsWith("__preview__")) : AssetDatabase.LoadAssetAtPath<AnimationClip>(path));
        }

        public static void EnsureFolderFor(string assetPath) =>
            EnsureDirectory(System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));

        public static void EnsureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/');
            EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
        }
    }
}
