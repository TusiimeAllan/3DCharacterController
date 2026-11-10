using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Turns any character model into a Humanoid the kit can animate:
    /// - already Humanoid: used as is;
    /// - rigged but Generic / Legacy: the model importer is switched to Humanoid and the bones mapped;
    /// - no skeleton (a static mesh, OBJ, scene object): auto-rigged by fitting the X Bot skeleton to its
    ///   shape and transferring X Bot's skin weights, saved as a new prefab. Works best on a character
    ///   standing in a T-pose; an A-pose or unusual proportions may need touching up.
    /// </summary>
    public static class CharacterPreparer
    {
        private const string Tag = "[CharacterKit]";

        public enum Status { Ready, ConvertToHumanoid, AutoRig, Unsupported }

        public enum Facing { PositiveZ, NegativeZ, PositiveX, NegativeX }

        public struct Report
        {
            public Status Status;
            public string Message;
            public float Height;
        }

        // ---------------------------------------------------------------- classify

        public static Report Inspect(GameObject model)
        {
            if (model == null) return new Report { Status = Status.Unsupported, Message = "Nothing selected." };
            float height = MeasureHeight(model);
            string size = height > 0f ? $" {height:F2} m tall." : "";
            float unit = UnitCorrection(height);
            string units = unit != 1f ? $" Looks like a units mix-up: it will be scaled x{unit:0.###} to {height * unit:F2} m." : "";

            var avatar = CharacterSetupBuilder.FindAvatar(model);
            if (avatar != null && avatar.isHuman && avatar.isValid)
                return new Report { Status = Status.Ready, Height = height, Message = "Humanoid, ready." + size + units };

            bool skinned = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(s => s.bones != null && s.bones.Length > 0);
            if (skinned)
            {
                var importer = SkinImporter(model);
                if (importer == null)
                    return new Report { Status = Status.Unsupported, Height = height, Message = "Rigged, but its model file could not be found to set it to Humanoid." };
                if (!Writable(importer.assetPath))
                    return new Report { Status = Status.Unsupported, Height = height, Message = "Rigged, but its model file is read-only." };
                return new Report { Status = Status.ConvertToHumanoid, Height = height, Message = "Rigged (" + importer.animationType + "): will be set to Humanoid." + size + units };
            }

            if (model.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh != null))
                return new Report { Status = Status.AutoRig, Height = height, Message = "No skeleton: will be auto-rigged from X Bot (best in a T-pose)." + size + units };

            return new Report { Status = Status.Unsupported, Height = height, Message = "No mesh found on this object." };
        }

        /// <summary>
        /// Makes the model Humanoid (converting or rigging as needed) and returns the model to put on the Player,
        /// or null with an error logged.
        /// </summary>
        /// <param name="targetHeight">Height to rig an unrigged model at; 0 keeps its own (unit corrected) size.</param>
        /// <param name="useKitSkeleton">Throw away the model's own bones (if any) and rig it with the kit's skeleton.</param>
        public static GameObject Prepare(GameObject model, string name, CharacterSetupProfile p, Facing facing, float targetHeight,
            bool useKitSkeleton = false)
        {
            var report = Inspect(model);
            if (useKitSkeleton && report.Status != Status.Unsupported) report.Status = Status.AutoRig;
            switch (report.Status)
            {
                case Status.Ready:
                    if (UnitCorrection(report.Height) != 1f && !FixUnits(model, report.Height)) return null;
                    return model;
                case Status.ConvertToHumanoid:
                    if (UnitCorrection(report.Height) != 1f && !FixUnits(model, report.Height)) return null;
                    return ConvertToHumanoid(model) ? model : null;
                case Status.AutoRig:
                    if (targetHeight <= 0f) targetHeight = report.Height * UnitCorrection(report.Height);
                    return AutoRig(model, name, p.RiggedCharactersFolder, facing, targetHeight);
                default:
                    Debug.LogError($"{Tag} {name}: {report.Message}", model);
                    return null;
            }
        }

        // ---------------------------------------------------------------- convert

        private static bool ConvertToHumanoid(GameObject model)
        {
            var importer = SkinImporter(model);
            if (importer == null) return false;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.optimizeGameObjects = false; // keep the bones for sockets and IK
            importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(importer.assetPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError($"{Tag} Unity could not map the bones of {importer.assetPath} to a humanoid automatically. " +
                               "Select it, open Rig > Configure and assign the missing bones, then run the wizard again.",
                    AssetDatabase.LoadMainAssetAtPath(importer.assetPath));
                return false;
            }

            // A prefab built on the model: give it an Animator with the new avatar.
            string path = AssetDatabase.GetAssetPath(model);
            if (path.EndsWith(".prefab"))
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                    animator.avatar = avatar;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log($"{Tag} {importer.assetPath} is now Humanoid.");
            return true;
        }

        /// <summary>
        /// A power of ten that brings a model modelled in the wrong units (centimetres read as metres and the like)
        /// to a human size, keeping its own height otherwise; 1 when the size is already plausible (0.3 to 4 m).
        /// </summary>
        public static float UnitCorrection(float height)
        {
            if (height <= 0f || (height >= 0.3f && height <= 4f)) return 1f;
            return Mathf.Pow(10f, Mathf.Round(Mathf.Log10(1.7f / height)));
        }

        /// <summary>Rescales a model file's import so it stands at a human size.</summary>
        private static bool FixUnits(GameObject model, float height)
        {
            var importer = SkinImporter(model);
            if (importer == null || !Writable(importer.assetPath))
            {
                Debug.LogWarning($"{Tag} {model.name} is {height:F2} m tall; set its import Scale Factor by hand.", model);
                return true; // still usable, just oversized
            }
            float k = UnitCorrection(height);
            importer.globalScale *= k;
            importer.SaveAndReimport();
            Debug.Log($"{Tag} {importer.assetPath} imported x{k:0.###} (was {height:F2} m, now {height * k:F2} m).");
            return true;
        }

        private static ModelImporter SkinImporter(GameObject model)
        {
            var direct = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(model)) as ModelImporter;
            if (direct != null) return direct;
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(smr.sharedMesh)) is ModelImporter mi) return mi;
            }
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(model);
            return source != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as ModelImporter : null;
        }

        private static bool Writable(string path)
        {
            if (path.StartsWith("Assets/")) return true;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            return info != null && (info.source == UnityEditor.PackageManager.PackageSource.Embedded ||
                                    info.source == UnityEditor.PackageManager.PackageSource.Local);
        }

        public static float MeasureHeight(GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return 0f;
            // Asset bounds are only valid on an instance.
            var instance = Object.Instantiate(model);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                float top = CharacterDimensions.MeasureHeight(instance.transform);
                if (top > 0f) return top;
                var rs = instance.GetComponentsInChildren<Renderer>(true);
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                return b.size.y;
            }
            finally { Object.DestroyImmediate(instance); }
        }

        // ---------------------------------------------------------------- auto-rig

        private struct Source
        {
            public Vector3[] Points;
            public BoneWeight[] Weights;
            public string[] BoneNames;
            public int[] Triangles;
        }

        /// <summary>
        /// Fits the X Bot skeleton to a static mesh and skins it by copying, for every vertex, the weights of
        /// the closest X Bot surface points (after fitting X Bot's bounds onto the character's). Saves the mesh,
        /// avatar and a prefab under the folder and returns the prefab.
        /// </summary>
        public static GameObject AutoRig(GameObject model, string name, string folder, Facing facing, float targetHeight)
        {
            var xbotAsset = AssetDatabase.LoadAssetAtPath<GameObject>(KitPaths.XBot);
            var xbotAvatar = xbotAsset != null ? CharacterSetupBuilder.FindAvatar(xbotAsset) : null;
            if (xbotAsset == null || xbotAvatar == null)
            {
                Debug.LogError($"{Tag} The bundled X Bot is missing, it is needed to auto-rig.");
                return null;
            }
            if (string.IsNullOrEmpty(name)) name = model.name;
            name = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars()));

            var xbot = Object.Instantiate(xbotAsset);
            xbot.hideFlags = HideFlags.HideAndDontSave;
            GameObject rig = null;
            try
            {
                xbot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // The character's mesh, combined, facing +Z with its feet at y = 0.
                var mesh = CombineMeshes(model, facing, out var materials);
                if (mesh == null) return null;

                // Match the model's pose: lower X Bot's arms until its silhouette is as wide (for its height)
                // as the model's, so a model in an A-pose gets its arm weights from arms, not from the air.
                var xAnimator = xbot.GetComponent<Animator>();
                var upperArms = new[] { xAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm), xAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm) };
                var tPose = upperArms.Select(t => t.rotation).ToArray();
                float targetRatio = mesh.bounds.size.x / Mathf.Max(1e-4f, mesh.bounds.size.y);
                float armDrop = 0f, bestError = float.MaxValue;
                for (float angle = 0f; angle <= 75f; angle += 5f)
                {
                    PoseArms(upperArms, tPose, angle);
                    var b = GeometryUtility.CalculateBounds(Template(xbot).Points, Matrix4x4.identity);
                    float error = Mathf.Abs(b.size.x / Mathf.Max(1e-4f, b.size.y) - targetRatio);
                    if (error < bestError) { bestError = error; armDrop = angle; }
                }
                PoseArms(upperArms, tPose, armDrop);
                var source = Template(xbot);
                var verts = mesh.vertices;
                var bounds = GeometryUtility.CalculateBounds(verts, Matrix4x4.identity);
                if (targetHeight > 0.1f && bounds.size.y > 1e-4f && Mathf.Abs(bounds.size.y - targetHeight) > 0.01f)
                {
                    float k = targetHeight / bounds.size.y;
                    for (int i = 0; i < verts.Length; i++) verts[i] *= k;
                    mesh.vertices = verts;
                    mesh.RecalculateBounds();
                    bounds = mesh.bounds;
                }
                // Feet on the ground, centred.
                Vector3 shift = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
                for (int i = 0; i < verts.Length; i++) verts[i] += shift;
                mesh.vertices = verts;
                mesh.RecalculateBounds();
                bounds = mesh.bounds;

                var xb = GeometryUtility.CalculateBounds(source.Points, Matrix4x4.identity);
                Vector3 scale = new Vector3(bounds.size.x / Mathf.Max(1e-4f, xb.size.x),
                    bounds.size.y / Mathf.Max(1e-4f, xb.size.y), bounds.size.z / Mathf.Max(1e-4f, xb.size.z));
                Vector3 xAnchor = new Vector3(xb.center.x, xb.min.y, xb.center.z);
                Vector3 tAnchor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

                // Where the body's sections are on each mesh (legs end at the crotch, the torso at the neck), so the
                // joints land on the model's own proportions: a big head gets a head-sized head, short legs short legs.
                var xMarks = FindLandmarks(SampleSurface(source.Points, source.Triangles, 60000), xb);
                var tMarks = FindLandmarks(SampleSurface(verts, mesh.triangles, 60000), bounds);
                var heights = PiecewiseHeights(xMarks, tMarks, xb, bounds);
                Debug.Log($"{Tag} {name}: " + (tMarks.Found
                    ? (float.IsNaN(tMarks.Crotch) ? "legs together, " : $"crotch {tMarks.Crotch - bounds.min.y:F2} m, ") + $"shoulders {tMarks.Shoulder - bounds.min.y:F2} m, neck {tMarks.Neck - bounds.min.y:F2} m of {bounds.size.y:F2} m."
                    : "body sections not recognised (robe, cape or unusual shape); X Bot's proportions are used."));
                Vector3 Fit(Vector3 p)
                {
                    var q = tAnchor + Vector3.Scale(p - xAnchor, scale);
                    q.y = heights(p.y);
                    return q;
                }

                // Skeleton: a copy of X Bot's, each joint moved to the fitted position.
                rig = new GameObject(name);
                var xHips = xbot.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
                var hips = Object.Instantiate(xHips.gameObject, rig.transform).transform;
                hips.name = xHips.name;
                var originals = xHips.GetComponentsInChildren<Transform>(true);
                var copies = hips.GetComponentsInChildren<Transform>(true);
                var fittedPositions = originals.Select(o => Fit(o.position)).ToArray();
                var indexOf = new Dictionary<Transform, int>();
                for (int i = 0; i < originals.Length; i++) indexOf[originals[i]] = i;
                for (int i = 0; i < copies.Length; i++)
                {
                    // Each bone keeps pointing along its limb (towards its only child) after the joints moved, so
                    // animation bends the limb where it bends instead of twisting it.
                    var o = originals[i];
                    Quaternion rotation = o.rotation;
                    if (o.childCount == 1 && indexOf.TryGetValue(o.GetChild(0), out int c))
                    {
                        Vector3 before = o.GetChild(0).position - o.position;
                        Vector3 after = fittedPositions[c] - fittedPositions[i];
                        if (before.sqrMagnitude > 1e-8f && after.sqrMagnitude > 1e-8f)
                            rotation = Quaternion.FromToRotation(before, after) * o.rotation;
                    }
                    copies[i].SetPositionAndRotation(fittedPositions[i], rotation);
                }
                var byName = new Dictionary<string, Transform>();
                foreach (var t in copies) byName[t.name] = t;
                var bones = source.BoneNames.Select(n => byName.TryGetValue(n, out var t) ? t : hips).ToArray();

                // Skin weights from the closest fitted X Bot points.
                var fitted = source.Points.Select(Fit).ToArray();
                mesh.boneWeights = TransferWeights(verts, mesh.triangles, fitted, source.Weights, bounds.size.y);
                mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * rig.transform.localToWorldMatrix).ToArray();
                mesh.name = name + "_Mesh";

                // Back to a T-pose: the avatar's reference pose (the mesh keeps its own pose through the bind poses).
                if (armDrop > 0f)
                {
                    var rigArms = new[] { byName[upperArms[0].name], byName[upperArms[1].name] };
                    PoseArms(rigArms, rigArms.Select(t => t.rotation).ToArray(), -armDrop);
                }

                var body = new GameObject(name + "_Body");
                body.transform.SetParent(rig.transform, false);
                var smr = body.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = bones;
                smr.rootBone = hips;
                smr.sharedMaterials = materials;
                smr.updateWhenOffscreen = false;
                smr.localBounds = new Bounds(hips.InverseTransformPoint(bounds.center), bounds.size * 1.2f);

                // Humanoid avatar with X Bot's bone mapping on the new skeleton.
                var hd = xbotAvatar.humanDescription;
                hd.skeleton = rig.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
                }).ToArray();
                var avatar = AvatarBuilder.BuildHumanAvatar(rig, hd);
                if (avatar == null || !avatar.isValid || !avatar.isHuman)
                {
                    Debug.LogError($"{Tag} Auto-rig of {name} failed to build a humanoid avatar.", model);
                    return null;
                }
                avatar.name = name + "_Avatar";
                var animator = rig.AddComponent<Animator>();
                animator.avatar = avatar;

                // Save.
                string dir = folder.TrimEnd('/') + "/" + name;
                AnimatorBuildUtil.EnsureDirectory(dir);
                string meshPath = dir + "/" + name + "_Mesh.asset";
                string avatarPath = dir + "/" + name + "_Avatar.asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.DeleteAsset(avatarPath);
                AssetDatabase.CreateAsset(mesh, meshPath);
                AssetDatabase.CreateAsset(avatar, avatarPath);
                var prefab = PrefabUtility.SaveAsPrefabAsset(rig, dir + "/" + name + ".prefab");
                AssetDatabase.SaveAssets();
                Debug.Log($"{Tag} Auto-rigged {name}: {verts.Length} vertices on {bones.Length} bones" +
                          (armDrop > 0f ? $", arms {armDrop:F0} deg below a T-pose" : ", T-pose") + $", saved to {dir}. " +
                          "Check the arms in Play mode; a character not in a T-pose may need its weights touched up.", prefab);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(xbot);
                if (rig != null) Object.DestroyImmediate(rig);
            }
        }

        private struct Landmarks
        {
            public bool Found;
            public float Crotch, Shoulder, Neck;
        }

        /// <summary>
        /// Finds the crotch, shoulders and neck from horizontal slices of the surface: the legs are two separate
        /// pieces up to the crotch, the neck is the narrowest part of the upper body, the shoulders are where it
        /// widens below the neck.
        /// </summary>
        private static Landmarks FindLandmarks(List<Vector3> points, Bounds b)
        {
            const int bins = 100;
            float h = b.size.y, y0 = b.min.y, cx = b.center.x;
            var xs = new List<float>[bins];
            for (int i = 0; i < bins; i++) xs[i] = new List<float>();
            foreach (var p in points) xs[Mathf.Clamp((int)((p.y - y0) / h * bins), 0, bins - 1)].Add(p.x - cx);

            // Width of the piece that crosses the centre line (0 where the centre line is in a gap, between the legs).
            float[] Widths(float gap)
            {
                var width = new float[bins];
                for (int k = 0; k < bins; k++)
                {
                    var l = xs[k];
                    if (l.Count == 0) continue;
                    l.Sort();
                    float lo = l[0], hi = l[0];
                    for (int i = 1; i <= l.Count; i++)
                    {
                        if (i < l.Count && l[i] - hi <= gap) { hi = l[i]; continue; }
                        if (lo <= 0f && hi >= 0f) { width[k] = hi - lo; break; }
                        if (i < l.Count) { lo = hi = l[i]; }
                    }
                }
                return width;
            }
            var w = Widths(h * 0.02f);
            float Smooth(int k) => (w[Mathf.Max(0, k - 1)] + w[k] + w[Mathf.Min(bins - 1, k + 1)]) / 3f;
            float MaxIn(int from, int to)
            {
                float m = 0f;
                for (int k = Mathf.Max(0, from); k <= Mathf.Min(bins - 1, to); k++) m = Mathf.Max(m, w[k]);
                return m;
            }
            float Y(int k) => y0 + (k + 0.5f) * h / bins;
            var none = new Landmarks();

            // Neck: the narrowest slice with a clearly wider head above it and clearly wider shoulders below it
            // (the narrow top of a head has nothing above, a waist has no head right above it).
            int neck = -1;
            float neckWidth = float.MaxValue;
            for (int k = (int)(bins * 0.3f); k < (int)(bins * 0.95f); k++)
            {
                float here = Smooth(k);
                if (here <= 0f || here >= neckWidth) continue;
                bool headAbove = MaxIn(k + 2, k + (int)(bins * 0.25f)) > here * 1.6f;
                bool shouldersBelow = MaxIn(k - (int)(bins * 0.15f), k - 2) > here * 1.5f;
                if (headAbove && shouldersBelow) { neck = k; neckWidth = here; }
            }
            if (neck < 0) return none;

            // Shoulders: going down from the neck, where the body first gets clearly wider.
            int shoulder = -1;
            for (int k = neck - 1; k > 0; k--)
                if (w[k] > neckWidth * 1.45f) { shoulder = k; break; }
            if (shoulder < 0) return none;

            // Crotch: where two separate legs join, with a fine gap so legs that nearly touch still count.
            var fine = Widths(h * 0.006f);
            int crotch = -1;
            bool legsApart = false;
            for (int k = (int)(bins * 0.03f); k < shoulder - (int)(bins * 0.08f); k++)
            {
                if (fine[k] <= 0f) { legsApart = true; continue; }
                if (legsApart && k + 2 < bins && fine[k + 1] > 0f && fine[k + 2] > 0f) { crotch = k; break; }
            }
            return new Landmarks
            {
                Found = true,
                Crotch = crotch > (int)(bins * 0.1f) ? Y(crotch) : float.NaN, // NaN: placed in proportion below
                Shoulder = Y(shoulder),
                Neck = Y(neck),
            };
        }

        /// <summary>
        /// Maps a height on X Bot to a height on the model section by section (feet to crotch, crotch to
        /// shoulders, shoulders to neck, neck to the top of the head); a plain stretch when a body was not read.
        /// </summary>
        private static System.Func<float, float> PiecewiseHeights(Landmarks x, Landmarks t, Bounds xb, Bounds tb)
        {
            if (!x.Found || !t.Found)
                return y => tb.min.y + (y - xb.min.y) * tb.size.y / Mathf.Max(1e-4f, xb.size.y);
            float xCrotch = float.IsNaN(x.Crotch) ? xb.min.y + (x.Shoulder - xb.min.y) * 0.62f : x.Crotch;
            // Legs that could not be told apart: the crotch sits where X Bot's does, in proportion to the shoulders.
            float tCrotch = float.IsNaN(t.Crotch)
                ? tb.min.y + (t.Shoulder - tb.min.y) * (xCrotch - xb.min.y) / Mathf.Max(1e-4f, x.Shoulder - xb.min.y)
                : t.Crotch;
            float[] from = { xb.min.y, xCrotch, x.Shoulder, x.Neck, xb.max.y };
            float[] to = { tb.min.y, tCrotch, t.Shoulder, t.Neck, tb.max.y };
            return y =>
            {
                int i = 0;
                while (i < from.Length - 2 && y > from[i + 1]) i++;
                float span = from[i + 1] - from[i];
                float k = span > 1e-5f ? (y - from[i]) / span : 0f;
                return to[i] + k * (to[i + 1] - to[i]);
            };
        }

        /// <summary>Points spread evenly over a surface (plus its vertices), so slices see the shape, not the tessellation.</summary>
        private static List<Vector3> SampleSurface(Vector3[] v, int[] t, int count)
        {
            var result = new List<Vector3>(v);
            int triangles = t.Length / 3;
            if (triangles == 0) return result;
            var cumulative = new float[triangles];
            float total = 0f;
            for (int i = 0; i < triangles; i++)
            {
                total += Vector3.Cross(v[t[i * 3 + 1]] - v[t[i * 3]], v[t[i * 3 + 2]] - v[t[i * 3]]).magnitude * 0.5f;
                cumulative[i] = total;
            }
            if (total <= 0f) return result;
            var random = new System.Random(12345);
            for (int n = 0; n < count; n++)
            {
                float pick = (float)random.NextDouble() * total;
                int i = System.Array.BinarySearch(cumulative, pick);
                if (i < 0) i = ~i;
                i = Mathf.Min(i, triangles - 1);
                float a = (float)random.NextDouble(), b = (float)random.NextDouble();
                if (a + b > 1f) { a = 1f - a; b = 1f - b; }
                var p0 = v[t[i * 3]];
                result.Add(p0 + a * (v[t[i * 3 + 1]] - p0) + b * (v[t[i * 3 + 2]] - p0));
            }
            return result;
        }

        /// <summary>
        /// Lowers both upper arms by the angle from their given rotations (around the body's forward axis,
        /// each towards its own side), or raises them for a negative angle.
        /// </summary>
        private static void PoseArms(Transform[] upperArms, Quaternion[] from, float angle)
        {
            for (int i = 0; i < upperArms.Length; i++)
            {
                if (upperArms[i] == null) continue;
                float side = upperArms[i].position.x < 0f ? 1f : -1f; // arm on the -x side swings down with +angle
                upperArms[i].rotation = Quaternion.AngleAxis(side * angle, Vector3.forward) * from[i];
            }
        }

        /// <summary>X Bot's skinned surface points in its bind pose, with their weights and bone names.</summary>
        private static Source Template(GameObject xbot)
        {
            var points = new List<Vector3>();
            var triangles = new List<int>();
            var weights = new List<BoneWeight>();
            var names = new List<string>();
            var index = new Dictionary<string, int>();
            foreach (var smr in xbot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var v = baked.vertices;
                var w = smr.sharedMesh.boneWeights;
                var local = new int[smr.bones.Length];
                for (int b = 0; b < smr.bones.Length; b++)
                {
                    string n = smr.bones[b] != null ? smr.bones[b].name : "";
                    if (!index.TryGetValue(n, out int id)) { id = names.Count; names.Add(n); index[n] = id; }
                    local[b] = id;
                }
                var m = smr.transform.localToWorldMatrix;
                int baseIndex = points.Count;
                foreach (int t in baked.triangles) triangles.Add(baseIndex + t);
                for (int i = 0; i < v.Length && i < w.Length; i++)
                {
                    points.Add(xbot.transform.InverseTransformPoint(m.MultiplyPoint3x4(v[i])));
                    var bw = w[i];
                    bw.boneIndex0 = local[bw.boneIndex0];
                    bw.boneIndex1 = local[bw.boneIndex1];
                    bw.boneIndex2 = local[bw.boneIndex2];
                    bw.boneIndex3 = local[bw.boneIndex3];
                    weights.Add(bw);
                }
                Object.DestroyImmediate(baked);
            }
            return new Source { Points = points.ToArray(), Weights = weights.ToArray(), BoneNames = names.ToArray(), Triangles = triangles.ToArray() };
        }

        /// <summary>
        /// Inverse distance blend of the 4 nearest template weights, keeping the 4 strongest bones. Pieces of the
        /// mesh that float away from the body (effects, accessories, loose plates) are bound rigidly to the one bone
        /// that dominates them, so they move as a piece instead of tearing between unrelated bones.
        /// </summary>
        private static BoneWeight[] TransferWeights(Vector3[] verts, int[] triangles, Vector3[] points, BoneWeight[] weights, float height)
        {
            var distance = new float[verts.Length];
            float cell = Mathf.Max(0.01f, height * 0.03f);
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            var grid = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < points.Length; i++)
            {
                var k = Key(points[i]);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }

            var result = new BoneWeight[verts.Length];
            var nearest = new List<(float d, int i)>(64);
            var accum = new Dictionary<int, float>();
            for (int v = 0; v < verts.Length; v++)
            {
                var p = verts[v];
                var c = Key(p);
                nearest.Clear();
                for (int r = 1; r <= 12 && nearest.Count < 4; r++)
                {
                    nearest.Clear();
                    for (int x = -r; x <= r; x++)
                    for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                    {
                        if (!grid.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list)) continue;
                        foreach (int i in list) nearest.Add(((points[i] - p).sqrMagnitude, i));
                    }
                }
                if (nearest.Count == 0)
                {
                    result[v] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                    continue;
                }
                nearest.Sort((a, b) => a.d.CompareTo(b.d));
                distance[v] = Mathf.Sqrt(nearest[0].d);
                accum.Clear();
                int take = Mathf.Min(4, nearest.Count);
                for (int n = 0; n < take; n++)
                {
                    float w = 1f / (nearest[n].d + 1e-6f);
                    var bw = weights[nearest[n].i];
                    Add(accum, bw.boneIndex0, bw.weight0 * w);
                    Add(accum, bw.boneIndex1, bw.weight1 * w);
                    Add(accum, bw.boneIndex2, bw.weight2 * w);
                    Add(accum, bw.boneIndex3, bw.weight3 * w);
                }
                var top = accum.Where(kv => kv.Value > 0f).OrderByDescending(kv => kv.Value).Take(4).ToArray();
                float sum = top.Sum(kv => kv.Value);
                var outW = new BoneWeight();
                if (top.Length > 0) { outW.boneIndex0 = top[0].Key; outW.weight0 = top[0].Value / sum; }
                if (top.Length > 1) { outW.boneIndex1 = top[1].Key; outW.weight1 = top[1].Value / sum; }
                if (top.Length > 2) { outW.boneIndex2 = top[2].Key; outW.weight2 = top[2].Value / sum; }
                if (top.Length > 3) { outW.boneIndex3 = top[3].Key; outW.weight3 = top[3].Value / sum; }
                result[v] = outW;
            }
            MakeLoosePiecesRigid(result, distance, triangles, verts.Length, height * 0.06f);
            return result;
        }

        /// <summary>Connected pieces whose vertices sit, on average, further than the threshold from the body get one bone.</summary>
        private static void MakeLoosePiecesRigid(BoneWeight[] weights, float[] distance, int[] triangles, int count, float threshold)
        {
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = Find(triangles[t]), b = Find(triangles[t + 1]), c = Find(triangles[t + 2]);
                parent[b] = a;
                parent[Find(c)] = a;
            }
            var pieces = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int r = Find(i);
                if (!pieces.TryGetValue(r, out var list)) pieces[r] = list = new List<int>();
                list.Add(i);
            }
            int rigid = 0;
            foreach (var piece in pieces.Values)
            {
                float mean = piece.Average(i => distance[i]);
                if (mean < threshold) continue;
                var votes = new Dictionary<int, float>();
                foreach (int i in piece)
                {
                    var w = weights[i];
                    Add(votes, w.boneIndex0, w.weight0);
                    Add(votes, w.boneIndex1, w.weight1);
                    Add(votes, w.boneIndex2, w.weight2);
                    Add(votes, w.boneIndex3, w.weight3);
                }
                int bone = votes.OrderByDescending(kv => kv.Value).First().Key;
                foreach (int i in piece) weights[i] = new BoneWeight { boneIndex0 = bone, weight0 = 1f };
                rigid++;
            }
            if (rigid > 0) Debug.Log($"{Tag} Auto-rig bound {rigid} loose piece(s) rigidly to one bone each.");
        }

        private static void Add(Dictionary<int, float> d, int bone, float w)
        {
            if (w <= 0f) return;
            d.TryGetValue(bone, out float current);
            d[bone] = current + w;
        }

        /// <summary>
        /// Every mesh under the model (static, or skinned in its bind pose) in one mesh with a submesh per material,
        /// in the model's frame turned to face +Z.
        /// </summary>
        private static Mesh CombineMeshes(GameObject model, Facing facing, out Material[] materials)
        {
            Quaternion turn = facing switch
            {
                Facing.NegativeZ => Quaternion.Euler(0f, 180f, 0f),
                Facing.PositiveX => Quaternion.Euler(0f, -90f, 0f),
                Facing.NegativeX => Quaternion.Euler(0f, 90f, 0f),
                _ => Quaternion.identity,
            };
            var instance = Object.Instantiate(model);
            instance.hideFlags = HideFlags.HideAndDontSave;
            var mats = new List<Material>();
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var toRoot = Matrix4x4.TRS(Vector3.zero, turn, Vector3.one) * instance.transform.worldToLocalMatrix;

                // Leave out what is not the character: flat ground planes and backdrops far wider than it is tall.
                // Static meshes, and skinned ones in their bind pose (their old bones are not used).
                var filters = instance.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null)
                    .Select(f => (Component)f)
                    .Concat(instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null))
                    .ToList();
                Mesh MeshOf(Component c) => c is MeshFilter mfc ? mfc.sharedMesh : ((SkinnedMeshRenderer)c).sharedMesh;
                var rendererOf = filters.ToDictionary(f => f, f => f.GetComponent<Renderer>());
                float tallest = filters.Select(f => rendererOf[f] != null ? rendererOf[f].bounds.size.y : 0f).DefaultIfEmpty(0f).Max();
                var skipped = filters.Where(f =>
                {
                    var r = rendererOf[f];
                    if (r == null) return false;
                    var b = r.bounds;
                    bool flat = b.size.y < tallest * 0.02f && Mathf.Max(b.size.x, b.size.z) > tallest * 0.3f;
                    bool backdrop = Mathf.Max(b.size.x, b.size.z) > tallest * 1.6f;
                    return flat || backdrop;
                }).ToList();
                if (skipped.Count > 0 && skipped.Count < filters.Count)
                    Debug.Log($"{Tag} Auto-rig leaves out {string.Join(", ", skipped.Select(f => f.transform.parent != null && f.transform.parent != instance.transform ? f.transform.parent.name : f.name))} (a ground plane or backdrop, not part of the body).");
                else skipped.Clear();
                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var colors = new List<Color>();
                var submeshes = new List<List<int>>();
                bool anyColors = false;
                foreach (var mf in filters)
                {
                    if (skipped.Contains(mf)) continue;
                    var src = MeshOf(mf);
                    if (!src.isReadable && !MakeReadable(src)) continue;
                    var mr = mf.GetComponent<Renderer>();
                    var m = toRoot * mf.transform.localToWorldMatrix;
                    int offset = vertices.Count;
                    var v = src.vertices;
                    var n = src.normals;
                    var uv = src.uv;
                    var col = src.colors;
                    anyColors |= col.Length == v.Length && v.Length > 0;
                    for (int i = 0; i < v.Length; i++)
                    {
                        vertices.Add(m.MultiplyPoint3x4(v[i]));
                        normals.Add(n.Length == v.Length ? m.MultiplyVector(n[i]).normalized : Vector3.up);
                        uvs.Add(uv.Length == v.Length ? uv[i] : Vector2.zero);
                        colors.Add(col.Length == v.Length ? col[i] : Color.white);
                    }
                    bool flip = m.determinant < 0f;
                    for (int s = 0; s < src.subMeshCount; s++)
                    {
                        var tris = src.GetTriangles(s);
                        var list = new List<int>(tris.Length);
                        for (int i = 0; i < tris.Length; i += 3)
                        {
                            list.Add(tris[i] + offset);
                            list.Add(tris[flip ? i + 2 : i + 1] + offset);
                            list.Add(tris[flip ? i + 1 : i + 2] + offset);
                        }
                        submeshes.Add(list);
                        var shared = mr != null ? mr.sharedMaterials : null;
                        mats.Add(shared != null && shared.Length > 0 ? shared[Mathf.Min(s, shared.Length - 1)] : null);
                    }
                }
                materials = mats.ToArray();
                if (vertices.Count == 0)
                {
                    Debug.LogError($"{Tag} {model.name} has no readable mesh to rig.", model);
                    return null;
                }
                var mesh = new Mesh { indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                if (anyColors) mesh.SetColors(colors);
                mesh.subMeshCount = submeshes.Count;
                for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }
            finally { Object.DestroyImmediate(instance); }
        }

        private static bool MakeReadable(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer) || !Writable(path)) return false;
            importer.isReadable = true;
            importer.SaveAndReimport();
            return true;
        }
    }
}
