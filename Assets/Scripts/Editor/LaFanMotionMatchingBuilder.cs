using System.Collections.Generic;
using System.IO;
using System.Linq;
using MxM;
using MxMEditor;
using UnityEditor;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Builds an MxM motion matching database (MxMAnimData) from the imported LaFAN1 takes.
    /// Settings come from the MxM demo database so the two are interchangeable at runtime:
    /// same pose joints (feet, neck, hands), trajectory (-0.67 s .. +1 s), 0.05 s pose interval, tag
    /// names (favour Walk / Run / Sprint) and calibration sets ("General", "Sprint").
    /// Every take is added twice, as recorded and mirrored, to double the coverage of turns.
    /// Idle comes from the demo idle sets (LaFAN1 has no dedicated idle take).
    ///
    /// Output goes to Assets/Art/MotionMatching/LaFAN1, which is git-ignored (LaFAN1 is CC BY-NC-ND 4.0).
    /// </summary>
    public static class LaFanMotionMatchingBuilder
    {
        private const string Tag = "[LaFanMM]";
        public const string OutputFolder = "Assets/Art/MotionMatching/LaFAN1";
        public const string AnimDataPath = OutputFolder + "/LaFAN1_AnimData.asset";
        private const string ModulePath = OutputFolder + "/LaFAN1_Locomotion.asset";
        private const string PreProcessPath = OutputFolder + "/LaFAN1_PreProcessData.asset";
        private const string DemoPreProcess = "Assets/Plugins/MotionMatching/Demo/Data/MotionMatching/MocapPreProcessData.asset";
        private const string DemoAnimData = "Assets/Plugins/MotionMatching/Demo/Data/MotionMatching/MocapAnimData.asset";

        // Favour tags in the demo tag module: Walk = Tag1, Run = Tag2, Sprint = Tag3.
        private static readonly (string category, ETags favour, string[] takes)[] Categories =
        {
            ("Walk", ETags.None, new[] { "walk1_subject1", "walk2_subject1", "walk3_subject1", "walk4_subject1" }),
            ("Run", ETags.Tag2, new[] { "run1_subject2", "run2_subject1" }),
            ("Sprint", ETags.Tag3, new[] { "sprint1_subject2" }),
        };

        [MenuItem("Tools/Character Controller/LaFAN1/Build Motion Matching Data")]
        public static void Build()
        {
            var demo = AssetDatabase.LoadAssetAtPath<MxMPreProcessData>(DemoPreProcess);
            var demoData = AssetDatabase.LoadAssetAtPath<MxMAnimData>(DemoAnimData);
            if (demo == null || demoData == null)
            {
                Debug.LogError($"{Tag} MxM demo pre-process data or anim data missing.");
                return;
            }

            // 1. Clips (and their mirrored twins).
            var clips = new Dictionary<string, (AnimationClip clip, AnimationClip mirror)>();
            foreach (var take in Categories.SelectMany(c => c.takes))
            {
                var pair = EnsureClips(take);
                if (pair.clip == null || pair.mirror == null)
                {
                    Debug.LogError($"{Tag} Take {take} is not imported. Run LaFAN1 > Convert Starter Set first.");
                    return;
                }
                clips[take] = pair;
            }

            Directory.CreateDirectory(OutputFolder);
            foreach (var p in new[] { ModulePath, PreProcessPath }) AssetDatabase.DeleteAsset(p);

            // 2. Animation module: one category per gait, every take recorded + mirrored.
            var module = ScriptableObject.CreateInstance<AnimationModule>();
            AssetDatabase.CreateAsset(module, ModulePath);
            var so = new SerializedObject(module);
            so.FindProperty("m_overrideConfigModule").objectReferenceValue = demo.OverrideConfigModule;
            so.FindProperty("m_overrideTagModule").objectReferenceValue = demo.OverrideTagModule;
            so.FindProperty("m_overrideEventModule").objectReferenceValue = demo.OverrideEventModule;
            so.ApplyModifiedPropertiesWithoutUndo();
            module.CompositeCategories.Clear();
            module.AnimationIdleSets.Clear();
            module.BlendSpaces.Clear();

            GameObject prefab = demo.OverrideConfigModule.Prefab;
            for (int c = 0; c < Categories.Length; c++)
            {
                var (name, favour, takes) = Categories[c];
                var category = new CompositeCategory(name)
                {
                    IgnoreEdges_default = false, Extrapolate_default = true, FavourTags_default = favour,
                };
                module.CompositeCategories.Add(category);
                foreach (var take in takes)
                foreach (var clip in new[] { clips[take].clip, clips[take].mirror })
                {
                    var comp = ScriptableObject.CreateInstance<MxMAnimationClipComposite>();
                    comp.name = clip.name + "_comp";
                    comp.SetPrimaryAnim(clip);
                    AssetDatabase.AddObjectToAsset(comp, module);
                    comp.hideFlags = HideFlags.HideInHierarchy;
                    comp.IgnoreEdges = category.IgnoreEdges_default;
                    comp.ExtrapolateTrajectory = category.Extrapolate_default;
                    comp.GlobalFavourTags = favour;
                    comp.TargetPreProcess = null;
                    comp.TargetAnimModule = module;
                    comp.TargetPrefab = prefab;
                    comp.CategoryId = c;
                    comp.ValidateBaseData();
                    category.Composites.Add(comp);
                    EditorUtility.SetDirty(comp);
                }
            }

            // Idle: reuse the demo walk module's idle sets.
            var demoIdle = demo.AnimationModules.Where(m => m != null && m.AnimationIdleSets != null)
                .SelectMany(m => m.AnimationIdleSets).Where(s => s != null).ToList();
            foreach (var src in demoIdle)
            {
                var idle = ScriptableObject.CreateInstance<MxMAnimationIdleSet>();
                idle.CopyData(src);
                idle.name = src.PrimaryClip.name + "_idle";
                idle.TargetPreProcess = null;
                idle.TargetAnimModule = module;
                AssetDatabase.AddObjectToAsset(idle, module);
                idle.hideFlags = HideFlags.HideInHierarchy;
                module.AnimationIdleSets.Add(idle);
                EditorUtility.SetDirty(idle);
            }
            EditorUtility.SetDirty(module);

            // 3. Pre-process data: a copy of the demo's settings, pointing at our module only.
            AssetDatabase.CopyAsset(DemoPreProcess, PreProcessPath);
            var pre = AssetDatabase.LoadAssetAtPath<MxMPreProcessData>(PreProcessPath);
            var pso = new SerializedObject(pre);
            var mods = pso.FindProperty("m_animModules");
            mods.ClearArray();
            mods.InsertArrayElementAtIndex(0);
            mods.GetArrayElementAtIndex(0).objectReferenceValue = module;
            pso.FindProperty("m_compositeCategories").ClearArray();
            pso.FindProperty("m_animIdleSets").ClearArray();
            pso.FindProperty("m_blendSpaces").ClearArray();
            pso.FindProperty("m_lastCreatedAnimData").objectReferenceValue = null;
            pso.FindProperty("m_generateModifiedClipsOnPreProcess").boolValue = false;
            pso.FindProperty("m_embedAnimClipsInAnimData").boolValue = false;
            pso.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            // 4. Pre-process (what MxM's inspector "Pre-Process" button does).
            var animData = AssetDatabase.LoadAssetAtPath<MxMAnimData>(AnimDataPath);
            bool existing = animData != null;
            if (!existing) animData = ScriptableObject.CreateInstance<MxMAnimData>();
            var processor = new MxMPreProcessor();
            processor.SetupSceneForProcessing(pre);
            processor.PreProcessData(animData);
            animData.InitializeCalibration(demoData.CalibrationSets.Select(cal => new CalibrationData(cal)).ToList());
            if (!existing) AssetDatabase.CreateAsset(animData, AnimDataPath);
            pre.LastSavedAnimData = animData;
            EditorUtility.SetDirty(pre);
            EditorUtility.SetDirty(animData);
            AssetDatabase.SaveAssets();

            Debug.Log($"{Tag} Built {AnimDataPath}: {Categories.Sum(c => c.takes.Length) * 2} composites " +
                      $"({clips.Values.Sum(p => p.clip.length) / 60f:F1} min x2 mirrored), {demoIdle.Count} idle sets, " +
                      $"{animData.Poses?.Length ?? 0} poses.");
        }

        /// <summary>The take's clip plus a mirrored "_MIRROR" clip (added to the import if missing).</summary>
        private static (AnimationClip clip, AnimationClip mirror) EnsureClips(string take)
        {
            string path = $"{LaFanImporter.OutputFolder}/{take}.fbx";
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return (null, null);
            var defs = (importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations).ToList();
            bool changed = false;
            if (defs.Count > 0 && defs.All(d => d.name != take + "_MIRROR"))
            {
                var src = defs.First(d => !d.mirror);
                var mirror = new ModelImporterClipAnimation();
                foreach (var f in typeof(ModelImporterClipAnimation).GetProperties().Where(p => p.CanRead && p.CanWrite))
                    f.SetValue(mirror, f.GetValue(src));
                mirror.name = take + "_MIRROR";
                mirror.mirror = true;
                defs.Add(mirror);
                changed = true;
            }
            // Root motion must come from the body: the FBX root node never moves, and a pose that keeps
            // the root offset drifts away from the character as the take goes on.
            foreach (var d in defs)
            {
                if (d.keepOriginalOrientation || d.keepOriginalPositionXZ || !d.keepOriginalPositionY ||
                    d.lockRootRotation || d.lockRootHeightY || d.lockRootPositionXZ || d.loopTime)
                {
                    d.keepOriginalOrientation = d.keepOriginalPositionXZ = false;
                    d.keepOriginalPositionY = true;
                    d.lockRootRotation = d.lockRootHeightY = d.lockRootPositionXZ = false;
                    d.loopTime = false;
                    changed = true;
                }
            }
            if (changed)
            {
                importer.clipAnimations = defs.ToArray();
                importer.SaveAndReimport();
            }
            var all = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>().ToList();
            return (all.FirstOrDefault(c => c.name == take), all.FirstOrDefault(c => c.name == take + "_MIRROR"));
        }
    }
}
