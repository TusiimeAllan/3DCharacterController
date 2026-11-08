using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// One-click assembly of the third person character controller:
    /// builds the Animator Controller from the imported Mixamo clips, creates the
    /// Player prefab (CharacterController + Animator + input + movement), and drops
    /// a playable rig into the current scene.
    /// </summary>
    public static class CharacterControllerSetup
    {
        private const string AnimDir = "Assets/Art/Animations";
        private const string XBotAnimDir = AnimDir + "/XBot";
        private const string XBotModel = "Assets/Art/Characters/XBot/X Bot.fbx";
        private const string ControllerPath = AnimDir + "/PlayerAnimator.controller";
        private const string PrefabPath = "Assets/Art/Characters/Player.prefab";
        private const string InputAsset = "Assets/Settings/Input/PlayerControls.inputactions";
        private const string ManInSuitModel = "Assets/Art/Characters/ManInSuit/Man In Suit@T-Pose.fbx";
        private const string SwitchScenePath = "Assets/Scenes/CharacterSwitch.unity";

        [MenuItem("Tools/Character Controller/1. Build Animator Controller")]
        public static AnimatorController BuildAnimator()
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("MotionSpeed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Jump", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FreeFall", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
            SetDefaultBool(controller, "Grounded", true);

            var sm = controller.layers[0].stateMachine;

            // --- Locomotion (standing) blend tree ---
            var locoState = controller.CreateBlendTreeInController("Locomotion", out var locoTree, 0);
            locoTree.blendType = BlendTreeType.Simple1D;
            locoTree.blendParameter = "Speed";
            locoTree.AddChild(Clip(XBotAnimDir + "/X Bot@Idle.fbx"), 0f);
            locoTree.AddChild(Clip(XBotAnimDir + "/X Bot@Walking.fbx"), 2f);
            locoTree.AddChild(Clip(XBotAnimDir + "/X Bot@Running.fbx"), 4f);
            locoTree.AddChild(Clip(XBotAnimDir + "/X Bot@Sprint.fbx"), 6f);

            // --- Crouch blend tree ---
            var crouchState = controller.CreateBlendTreeInController("Crouch", out var crouchTree, 0);
            crouchTree.blendType = BlendTreeType.Simple1D;
            crouchTree.blendParameter = "Speed";
            crouchTree.AddChild(Clip(XBotAnimDir + "/X Bot@Crouching Idle.fbx"), 0f);
            crouchTree.AddChild(Clip(XBotAnimDir + "/X Bot@Crouch Walk.fbx"), 1.5f);

            // --- Air states ---
            var jumpState = sm.AddState("Jump");
            jumpState.motion = Clip(XBotAnimDir + "/X Bot@Jump.fbx");
            var fallState = sm.AddState("Fall");
            fallState.motion = Clip(XBotAnimDir + "/X Bot@Falling.fbx");

            sm.defaultState = locoState;

            // Locomotion <-> Crouch
            AddTransition(locoState, crouchState, false, 0.15f, ("Crouch", true));
            AddTransition(crouchState, locoState, false, 0.15f, ("Crouch", false));

            // Ground -> Jump
            AddTransition(locoState, jumpState, false, 0.1f, ("Jump", true));

            // Jump -> Fall (after the jump pose plays out)
            var jumpToFall = jumpState.AddTransition(fallState);
            jumpToFall.hasExitTime = true;
            jumpToFall.exitTime = 0.7f;
            jumpToFall.duration = 0.2f;

            // Walk off a ledge -> Fall
            AddTransition(locoState, fallState, false, 0.2f, ("FreeFall", true));

            // Land
            AddTransition(fallState, locoState, false, 0.15f, ("Grounded", true));
            AddTransition(jumpState, locoState, false, 0.15f, ("Grounded", true));

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CharacterController] Animator built at {ControllerPath}");
            return controller;
        }

        [MenuItem("Tools/Character Controller/2. Build Player Prefab")]
        public static GameObject BuildPlayerPrefab()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                             ?? BuildAnimator();

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(XBotModel);
            if (modelAsset == null)
            {
                Debug.LogError($"[CharacterController] X Bot model not found at {XBotModel}. " +
                               "Let Unity finish importing first.");
                return null;
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(XBotModel)
                .OfType<Avatar>().FirstOrDefault();

            var root = new GameObject("Player");

            var cc = root.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.height = 1.8f;
            cc.radius = 0.28f;
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.3f;

            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            model.name = "Mesh";
            model.transform.SetParent(root.transform, false);
            // The FBX root ships its own Animator; the controller drives one on the Player root instead.
            if (model.TryGetComponent<Animator>(out var childAnim)) Object.DestroyImmediate(childAnim);

            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = avatar;
            animator.applyRootMotion = false;

            root.AddComponent<PlayerInputs>();
            var tpc = root.AddComponent<ThirdPersonController>();
            tpc.GroundLayers = LayerMask.GetMask("Default");

            // Head-height socket the camera orbits around.
            var camRoot = new GameObject("CameraRoot");
            camRoot.transform.SetParent(root.transform, false);
            camRoot.transform.localPosition = new Vector3(0f, 1.6f, 0f);

#if ENABLE_INPUT_SYSTEM
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputAsset);
            var pi = root.AddComponent<PlayerInput>();
            pi.actions = actions;
            pi.defaultActionMap = "Player";
            pi.notificationBehavior = PlayerNotifications.SendMessages;
#endif

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CharacterController] Player prefab built at {PrefabPath}");
            return prefab;
        }

        [MenuItem("Tools/Character Controller/3. Setup Playable Scene")]
        public static void SetupScene()
        {
            if (PopulateScene() == null) return;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[CharacterController] Scene ready. Press Play.");
        }

        [MenuItem("Tools/Character Controller/4. Build Character Switch Scene")]
        public static void BuildCharacterSwitchScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var player = PopulateScene();
            if (player == null) return;

            var switcher = player.AddComponent<CharacterSwitcher>();
            switcher.Characters = new[]
            {
                Entry("X Bot", XBotModel),
                Entry("Man In Suit", ManInSuitModel),
            }.Where(e => e != null).ToArray();

            EditorSceneManager.SaveScene(scene, SwitchScenePath);

            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (buildScenes.All(s => s.path != SwitchScenePath))
            {
                buildScenes.Add(new EditorBuildSettingsScene(SwitchScenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }

            Debug.Log($"[CharacterController] Character switch scene saved at {SwitchScenePath}. " +
                      "Press Play, then Tab / 1-2 to switch.");
        }

        private static CharacterSwitcher.CharacterEntry Entry(string name, string modelPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null)
            {
                Debug.LogWarning($"[CharacterController] Model not found at {modelPath}, skipping.");
                return null;
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman)
                Debug.LogWarning($"[CharacterController] {modelPath} has no valid Humanoid avatar.");
            return new CharacterSwitcher.CharacterEntry { Name = name, Model = model, Avatar = avatar };
        }

        private static GameObject PopulateScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) ?? BuildPlayerPrefab();
            if (prefab == null) return null;

            // Ground.
            if (Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length == 0 &&
                GameObject.Find("Ground") == null)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = new Vector3(10f, 1f, 10f);
            }

            // Player.
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            player.transform.position = new Vector3(0f, 0.1f, 0f);

            // Light.
            if (Object.FindObjectsByType<Light>(FindObjectsSortMode.None).All(l => l.type != LightType.Directional))
            {
                var sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                sun.shadows = LightShadows.Soft;
            }

            // Camera.
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.gameObject.AddComponent<AudioListener>();
            }
            var tpCam = cam.GetComponent<ThirdPersonCamera>() ?? cam.gameObject.AddComponent<ThirdPersonCamera>();
            tpCam.Target = player.transform.Find("CameraRoot");
            tpCam.Input = player.GetComponent<PlayerInputs>();
            return player;
        }

        [MenuItem("Tools/Character Controller/Build Everything")]
        public static void BuildEverything()
        {
            BuildAnimator();
            BuildPlayerPrefab();
            SetupScene();
        }

        // --- helpers ---

        private static Motion Clip(string fbxPath)
        {
            var clip = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null)
                Debug.LogWarning($"[CharacterController] No AnimationClip found in {fbxPath}");
            return clip;
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, bool hasExitTime,
            float duration, params (string param, bool val)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = hasExitTime;
            t.duration = duration;
            t.canTransitionToSelf = false;
            foreach (var (param, val) in conditions)
                t.AddCondition(val ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, param);
        }

        private static void SetDefaultBool(AnimatorController controller, string name, bool value)
        {
            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == name)
                {
                    parameters[i].defaultBool = value;
                    controller.parameters = parameters;
                    return;
                }
            }
        }
    }
}
