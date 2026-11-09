using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Builds the Showroom test scene from Assets/Art/Showroom/Reference/CarShowroom.jpg:
    /// a pale blue-grey room with a soft floor-to-wall cove, a dark ceiling with a large
    /// glowing light-panel grid, cool soft lighting, haze and a cinematic grade.
    /// The Player rig and camera are carried over from the Main scene. Re-running rebuilds the room.
    /// </summary>
    public static class ShowroomSceneBuilder
    {
        private const string Tag = "[ShowroomSceneBuilder]";
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string ScenePath = "Assets/Scenes/Showroom.unity";
        private const string ArtRoot = "Assets/Art/Showroom";
        private const string VolumeProfilePath = "Assets/Settings/ShowroomVolumeProfile.asset";
        private const string RoomRootName = "Showroom";

        // Room dimensions in meters. Floor top is at y = 0.
        private const float Size = 20f;
        private const float Height = 6f;
        private const float WallThickness = 0.4f;
        private const float CoveRadius = 1.2f;
        private const int CoveSegments = 16;

        // Ceiling light-panel grid.
        private const int PanelCount = 5;
        private const float PanelArea = 13f;
        private const float PanelGap = 0.12f;

        [MenuItem("Tools/Character Controller/Build Showroom Scene")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null &&
                !AssetDatabase.CopyAsset(MainScenePath, ScenePath))
            {
                Debug.LogError($"{Tag} Could not copy {MainScenePath} to {ScenePath}.");
                return;
            }

            // Open additively so whatever is open (and possibly unsaved) is never discarded.
            var previousActive = EditorSceneManager.GetActiveScene();
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            bool wasLoaded = scene.IsValid() && scene.isLoaded;
            if (!wasLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene); // new objects and RenderSettings go to this scene

            try
            {
                // Remove the test ground, the sun and any previous build of the room.
                foreach (var go in scene.GetRootGameObjects())
                    if (go.name == "Ground" || go.name == RoomRootName) Object.DestroyImmediate(go);
                foreach (var l in InScene<Light>(scene))
                    if (l != null && l.type == LightType.Directional) Object.DestroyImmediate(l.gameObject);

                var mats = BuildMaterials();
                var root = new GameObject(RoomRootName).transform;

                BuildShell(root, mats);
                BuildCoves(root, mats.Wall);
                BuildLightPanels(root, mats);
                BuildLights(root);
                ConfigureEnvironment();
                ConfigureVolume(scene);
                PlacePlayerAndCamera(scene);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AddToBuildSettings();
                Debug.Log($"{Tag} Showroom scene built and saved at {ScenePath}.");
            }
            finally
            {
                if (!wasLoaded && previousActive.IsValid() && previousActive != scene)
                {
                    EditorSceneManager.SetActiveScene(previousActive);
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static T[] InScene<T>(UnityEngine.SceneManagement.Scene scene) where T : Component =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();

        // ---------------------------------------------------------------- Materials

        private struct Materials
        {
            public Material Floor, Wall, Ceiling, Panel, Frame;
        }

        private static Materials BuildMaterials()
        {
            return new Materials
            {
                Floor = LitMaterial("Showroom_Floor", new Color(0.80f, 0.83f, 0.84f), 0.45f),
                Wall = LitMaterial("Showroom_Wall", new Color(0.80f, 0.84f, 0.85f), 0.1f),
                Ceiling = LitMaterial("Showroom_Ceiling", new Color(0.035f, 0.05f, 0.06f), 0.2f),
                Frame = LitMaterial("Showroom_PanelFrame", new Color(0.12f, 0.15f, 0.17f), 0.3f),
                Panel = EmissiveMaterial("Showroom_LightPanel", new Color(0.92f, 0.97f, 1f), 3.2f),
            };
        }

        private static Material LoadOrCreate(string name)
        {
            string path = $"{ArtRoot}/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            EnsureFolder($"{ArtRoot}/Materials");
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static Material LitMaterial(string name, Color color, float smoothness)
        {
            var mat = LoadOrCreate(name);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material EmissiveMaterial(string name, Color color, float intensity)
        {
            var mat = LitMaterial(name, color, 0.5f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * intensity);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------------------------------------------------------------- Geometry

        private static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat,
            bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static void BuildShell(Transform root, Materials m)
        {
            float half = Size * 0.5f;
            float t = WallThickness;
            float outer = Size + t * 2f;

            Box("Floor", root, new Vector3(0f, -t * 0.5f, 0f), new Vector3(outer, t, outer), m.Floor);
            Box("Ceiling", root, new Vector3(0f, Height + t * 0.5f, 0f), new Vector3(outer, t, outer), m.Ceiling);

            var walls = new GameObject("Walls").transform;
            walls.SetParent(root, false);
            float y = Height * 0.5f;
            Box("Wall_North", walls, new Vector3(0f, y, half + t * 0.5f), new Vector3(outer, Height, t), m.Wall);
            Box("Wall_South", walls, new Vector3(0f, y, -half - t * 0.5f), new Vector3(outer, Height, t), m.Wall);
            Box("Wall_East", walls, new Vector3(half + t * 0.5f, y, 0f), new Vector3(t, Height, Size), m.Wall);
            Box("Wall_West", walls, new Vector3(-half - t * 0.5f, y, 0f), new Vector3(t, Height, Size), m.Wall);
        }

        /// <summary>Concave quarter-round strips that blend the floor into each wall, like a photo studio cove.</summary>
        private static void BuildCoves(Transform root, Material wall)
        {
            var mesh = BuildCoveMesh();
            var coves = new GameObject("Coves").transform;
            coves.SetParent(root, false);
            float half = Size * 0.5f;

            void Cove(string name, Vector3 pos, float yaw)
            {
                var go = new GameObject(name);
                go.transform.SetParent(coves, false);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = wall;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            Cove("Cove_North", new Vector3(0f, 0f, half), 0f);
            Cove("Cove_South", new Vector3(0f, 0f, -half), 180f);
            Cove("Cove_East", new Vector3(half, 0f, 0f), 90f);
            Cove("Cove_West", new Vector3(-half, 0f, 0f), -90f);
        }

        private static Mesh BuildCoveMesh()
        {
            string path = $"{ArtRoot}/Meshes/Cove.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                EnsureFolder($"{ArtRoot}/Meshes");
                mesh = new Mesh { name = "Cove" };
                AssetDatabase.CreateAsset(mesh, path);
            }
            mesh.Clear();

            // Local space: the wall plane is z = 0, the room is towards -z, the strip runs along x.
            float halfLen = Size * 0.5f;
            int rows = CoveSegments + 1;
            var verts = new Vector3[rows * 2];
            var normals = new Vector3[rows * 2];
            var uvs = new Vector2[rows * 2];
            float arc = Mathf.PI * 0.5f * CoveRadius;

            for (int i = 0; i < rows; i++)
            {
                float a = (i / (float)CoveSegments) * Mathf.PI * 0.5f;
                float z = -CoveRadius + CoveRadius * Mathf.Sin(a);
                float y = CoveRadius - CoveRadius * Mathf.Cos(a);
                var n = new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a));
                float v = arc * i / CoveSegments;

                verts[i * 2] = new Vector3(-halfLen, y, z);
                verts[i * 2 + 1] = new Vector3(halfLen, y, z);
                normals[i * 2] = normals[i * 2 + 1] = n;
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(Size, v);
            }

            var tris = new int[CoveSegments * 6];
            for (int i = 0; i < CoveSegments; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                int k = i * 6;
                tris[k] = a; tris[k + 1] = c; tris[k + 2] = b;
                tris[k + 3] = b; tris[k + 4] = c; tris[k + 5] = d;
            }

            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private static void BuildLightPanels(Transform root, Materials m)
        {
            var grid = new GameObject("LightPanels").transform;
            grid.SetParent(root, false);

            // Recessed frame tray behind the panels gives the dark grid lines.
            Box("PanelFrame", grid, new Vector3(0f, Height - 0.03f, 0f),
                new Vector3(PanelArea + 0.4f, 0.06f, PanelArea + 0.4f), m.Frame, false);

            float cell = PanelArea / PanelCount;
            float panel = cell - PanelGap;
            float start = -PanelArea * 0.5f + cell * 0.5f;
            for (int x = 0; x < PanelCount; x++)
            for (int z = 0; z < PanelCount; z++)
            {
                Box($"Panel_{x}_{z}", grid, new Vector3(start + x * cell, Height - 0.08f, start + z * cell),
                    new Vector3(panel, 0.04f, panel), m.Panel, false);
            }
        }

        private static void BuildLights(Transform root)
        {
            var lights = new GameObject("Lights").transform;
            lights.SetParent(root, false);

            // One central shadow-casting key (a single soft shadow, as under the car in the reference)
            // plus four shadowless fills under the panel grid that stand in for the big area light.
            Light Spot(string name, Vector3 p, float intensity, float angle, bool shadows)
            {
                var go = new GameObject(name);
                go.transform.SetParent(lights, false);
                go.transform.localPosition = new Vector3(p.x, Height - 0.3f, p.z);
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = new Color(0.9f, 0.97f, 1f);
                l.intensity = intensity;
                l.range = Height * 3f;
                l.spotAngle = angle;
                l.innerSpotAngle = angle * 0.5f;
                l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
                l.shadowStrength = 0.6f;
                return l;
            }

            Spot("KeySpot", Vector3.zero, 22f, 150f, true);

            // Shadowless point fills just under the panels also wash the walls, keeping them pale like the photo.
            float o = PanelArea * 0.3f;
            int i = 0;
            foreach (var p in new[] { new Vector3(-o, 0, -o), new Vector3(o, 0, -o), new Vector3(-o, 0, o), new Vector3(o, 0, o) })
            {
                var go = new GameObject($"Fill_{i++}");
                go.transform.SetParent(lights, false);
                go.transform.localPosition = new Vector3(p.x, Height - 0.6f, p.z);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(0.9f, 0.97f, 1f);
                l.intensity = 9f;
                l.range = Size;
                l.shadows = LightShadows.None;
            }
            // No realtime reflection probe: in edit mode URP's ReflectionProbeManager throws on an
            // unrendered realtime probe and breaks the whole frame.
        }

        // ---------------------------------------------------------------- Environment

        private static void ConfigureEnvironment()
        {
            RenderSettings.skybox = null;
            RenderSettings.sun = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.58f, 0.60f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.52f, 0.54f);
            RenderSettings.ambientGroundColor = new Color(0.34f, 0.38f, 0.40f);

            // Light haze that softens the far walls, as in the reference.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.66f, 0.72f, 0.74f);
            RenderSettings.fogDensity = 0.018f;
        }

        private static void ConfigureVolume(UnityEngine.SceneManagement.Scene scene)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            }

            T Get<T>() where T : VolumeComponent
            {
                if (!profile.TryGet(out T c))
                {
                    c = profile.Add<T>(false);
                    c.name = typeof(T).Name;
                    AssetDatabase.AddObjectToAsset(c, profile);
                }
                return c;
            }

            var tone = Get<Tonemapping>();
            tone.mode.Override(TonemappingMode.ACES);

            var bloom = Get<Bloom>();
            bloom.threshold.Override(0.95f);
            bloom.intensity.Override(1.4f);
            bloom.scatter.Override(0.8f);
            bloom.tint.Override(new Color(0.85f, 0.95f, 1f));

            var color = Get<ColorAdjustments>();
            color.postExposure.Override(0.6f);
            color.contrast.Override(12f);
            color.saturation.Override(-30f);
            color.colorFilter.Override(new Color(0.93f, 0.99f, 1f));

            var wb = Get<WhiteBalance>();
            wb.temperature.Override(-10f);
            wb.tint.Override(-6f);

            var smh = Get<ShadowsMidtonesHighlights>();
            smh.shadows.Override(new Vector4(0.9f, 1f, 1.05f, -0.05f));

            var vignette = Get<Vignette>();
            vignette.intensity.Override(0.32f);
            vignette.smoothness.Override(0.5f);

            var grain = Get<FilmGrain>();
            grain.type.Override(FilmGrainLookup.Medium1);
            grain.intensity.Override(0.3f);
            grain.response.Override(0.8f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var volume = InScene<Volume>(scene).FirstOrDefault(v => v.isGlobal);
            if (volume == null)
            {
                volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
            }
            volume.sharedProfile = profile;
            volume.priority = 1f;
        }

        private static void PlacePlayerAndCamera(UnityEngine.SceneManagement.Scene scene)
        {
            var player = InScene<ThirdPersonController>(scene).FirstOrDefault();
            if (player != null)
            {
                // Stand off-centre facing the far corner, the same view as the reference photo.
                player.transform.SetPositionAndRotation(new Vector3(-2f, 0f, -2f), Quaternion.Euler(0f, 45f, 0f));
            }

            var cam = InScene<Camera>(scene).FirstOrDefault(c => c.CompareTag("MainCamera"));
            if (cam == null) return;
            cam.transform.rotation = Quaternion.Euler(10f, 45f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.07f, 0.08f);
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            EditorUtility.SetDirty(cam);
            EditorUtility.SetDirty(data);
        }

        // ---------------------------------------------------------------- Helpers

        private static void AddToBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == ScenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)!.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
