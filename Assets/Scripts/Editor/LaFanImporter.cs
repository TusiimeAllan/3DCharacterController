using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Brings Ubisoft LaFAN1 motion capture (BVH) into the project for motion matching:
    /// Blender (headless, Tools/LaFAN1/bvh2fbx.py) converts each BVH to an FBX in metres, then Unity imports
    /// it as Humanoid with body-based root motion (see MixamoModelPostprocessor).
    ///
    /// LaFAN1 is CC BY-NC-ND 4.0: personal, non-commercial use only, and nothing converted from it may be
    /// shared. The BVHs (ExternalData/lafan1) and every converted asset are git-ignored for that reason.
    /// </summary>
    public static class LaFanImporter
    {
        private const string Tag = "[LaFanImporter]";
        public const string OutputFolder = "Assets/Art/Animations/LaFAN1";
        private const string BvhFolderPref = "LaFAN1.BvhFolder";
        private const string BlenderPref = "LaFAN1.Blender";

        /// <summary>Locomotion-focused starting set, one performer per take.</summary>
        public static readonly string[] StarterSet =
        {
            "walk1_subject1", "walk2_subject1", "walk3_subject1", "walk4_subject1",
            "run1_subject2", "run2_subject1", "sprint1_subject2",
            "aiming1_subject1", "aiming2_subject2",
            "ground1_subject1", "ground2_subject2",
            "jumps1_subject1", "fallAndGetUp1_subject1",
        };

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static string BvhFolder
        {
            get => EditorPrefs.GetString(BvhFolderPref, Path.Combine(ProjectRoot, "ExternalData", "lafan1"));
            set => EditorPrefs.SetString(BvhFolderPref, value);
        }

        public static string Blender
        {
            get => EditorPrefs.GetString(BlenderPref, FindBlender());
            set => EditorPrefs.SetString(BlenderPref, value);
        }

        [MenuItem("Tools/Character Controller/LaFAN1/Convert Starter Set (Blender)")]
        public static void ConvertStarterSet() => Convert(StarterSet);

        [MenuItem("Tools/Character Controller/LaFAN1/Convert All 77 Takes (Blender)")]
        public static void ConvertAll() =>
            Convert(Directory.GetFiles(BvhFolder, "*.bvh").Select(Path.GetFileNameWithoutExtension).ToArray());

        /// <summary>Converts the named takes that are not converted yet. Returns how many were converted.</summary>
        public static int Convert(string[] takes)
        {
            if (string.IsNullOrEmpty(Blender) || !File.Exists(Blender))
            {
                Debug.LogError($"{Tag} Blender not found. Set it with EditorPrefs key {BlenderPref}.");
                return 0;
            }
            string outDir = Path.Combine(ProjectRoot, OutputFolder);
            var todo = takes.Where(t => !File.Exists(Path.Combine(outDir, t + ".fbx")))
                .Select(t => Path.Combine(BvhFolder, t + ".bvh"))
                .ToArray();
            var missing = todo.Where(f => !File.Exists(f)).ToArray();
            if (missing.Length > 0)
            {
                Debug.LogError($"{Tag} BVH not found: {string.Join(", ", missing)} (folder {BvhFolder}).");
                return 0;
            }
            if (todo.Length == 0)
            {
                Debug.Log($"{Tag} All requested takes are already converted.");
                return 0;
            }

            string script = Path.Combine(ProjectRoot, "Tools", "LaFAN1", "bvh2fbx.py");
            string args = $"-b --factory-startup -P \"{script}\" -- \"{outDir}\" " +
                          string.Join(" ", todo.Select(f => $"\"{f}\""));
            var psi = new ProcessStartInfo(Blender, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            int converted = 0;
            try
            {
                EditorUtility.DisplayProgressBar("LaFAN1", $"Converting {todo.Length} takes with Blender", 0.1f);
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    converted = output.Split('\n').Count(l => l.StartsWith("CONVERTED"));
                    if (converted < todo.Length) Debug.LogWarning($"{Tag} Blender output:\n{output}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            Debug.Log($"{Tag} Converted {converted}/{todo.Length} takes into {OutputFolder}.");
            return converted;
        }

        private static string FindBlender()
        {
            const string root = @"C:\Program Files\Blender Foundation";
            if (!Directory.Exists(root)) return "";
            return Directory.GetDirectories(root)
                .Select(d => Path.Combine(d, "blender.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p)
                .FirstOrDefault() ?? "";
        }
    }
}
