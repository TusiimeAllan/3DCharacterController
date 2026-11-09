using System.IO;
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
    /// Builds the Animator Controller, Player prefab and scene rig from a
    /// <see cref="CharacterSetupProfile"/>. Invoked from the profile's Inspector buttons.
    /// </summary>
    public static class CharacterSetupBuilder
    {
        private const string Tag = "[CharacterController]";

        // ---------------------------------------------------------------- Animator

        public static AnimatorController BuildAnimator(CharacterSetupProfile p)
        {
            EnsureFolder(p.ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(p.ControllerPath);

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("MotionSpeed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Jump", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FreeFall", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
            SetDefaultBool(controller, "Grounded", true);

            var sm = controller.layers[0].stateMachine;

            var locoState = controller.CreateBlendTreeInController("Locomotion", out var locoTree, 0);
            locoTree.blendType = BlendTreeType.Simple1D;
            locoTree.blendParameter = "Speed";
            locoTree.useAutomaticThresholds = false;
            AddChild(locoTree, p.Idle, 0f, nameof(p.Idle));
            AddChild(locoTree, p.Walk, p.WalkThreshold, nameof(p.Walk));
            AddChild(locoTree, p.Run, p.RunThreshold, nameof(p.Run));
            AddChild(locoTree, p.Sprint, p.SprintThreshold, nameof(p.Sprint));

            var crouchState = controller.CreateBlendTreeInController("Crouch", out var crouchTree, 0);
            crouchTree.blendType = BlendTreeType.Simple1D;
            crouchTree.blendParameter = "Speed";
            crouchTree.useAutomaticThresholds = false;
            AddChild(crouchTree, p.CrouchIdle, 0f, nameof(p.CrouchIdle));
            AddChild(crouchTree, p.CrouchWalk, p.CrouchWalkThreshold, nameof(p.CrouchWalk));

            var jumpState = sm.AddState("Jump");
            jumpState.motion = Warn(p.Jump, nameof(p.Jump));
            var fallState = sm.AddState("Fall");
            fallState.motion = Warn(p.Fall, nameof(p.Fall));

            sm.defaultState = locoState;

            AddTransition(locoState, crouchState, p.CrouchTransition, ("Crouch", true));
            AddTransition(crouchState, locoState, p.CrouchTransition, ("Crouch", false));
            AddTransition(locoState, jumpState, p.JumpTransition, ("Jump", true));

            var jumpToFall = jumpState.AddTransition(fallState);
            jumpToFall.hasExitTime = true;
            jumpToFall.exitTime = p.JumpToFallExitTime;
            jumpToFall.duration = p.JumpToFallTransition;

            AddTransition(locoState, fallState, p.FallTransition, ("FreeFall", true));
            AddTransition(fallState, locoState, p.LandTransition, ("Grounded", true));
            AddTransition(jumpState, locoState, p.LandTransition, ("Grounded", true));

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AirborneStatesBuilder.Apply(controller); // UAL jump start/loop/land and hard-landing roll
            CrouchStatesBuilder.Apply(controller); // UAL crouch idle/walk matched to the crouch speed
            ActionStatesBuilder.Apply(controller); // hits, death, sit, interact, punches, emotes, push
            StrafeStatesBuilder.Apply(controller); // face the camera and move in any direction
            ParkourBuilder.ApplyAnimator(controller); // hurdle, climb and drop states
            SlopeStatesBuilder.Apply(controller); // slide pose for slopes over the limit
            Debug.Log($"{Tag} Animator built at {p.ControllerPath}", controller);
            return controller;
        }

        // ---------------------------------------------------------------- Prefab

        public static GameObject BuildPlayerPrefab(CharacterSetupProfile p)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(p.ControllerPath)
                             ?? BuildAnimator(p);

            var models = p.Characters.Where(c => c != null && c.Model != null).ToList();
            if (models.Count == 0)
            {
                Debug.LogError($"{Tag} Add at least one character model to the profile.", p);
                return null;
            }

            var root = new GameObject("Player");

            var cc = root.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, p.Height * 0.5f, 0f);
            cc.height = p.Height;
            cc.radius = p.Radius;
            cc.slopeLimit = p.SlopeLimit;
            cc.stepOffset = p.StepOffset;

            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            var inputs = root.AddComponent<PlayerInputs>();

            var tpc = root.AddComponent<ThirdPersonController>();
            tpc.GroundLayers = p.GroundLayers;
            tpc.StandingHeight = p.Height;
            tpc.GroundedRadius = p.Radius;
            var tpcSo = new SerializedObject(tpc);
            tpcSo.FindProperty("_controller").objectReferenceValue = cc;
            tpcSo.FindProperty("_input").objectReferenceValue = inputs;
            tpcSo.FindProperty("_animator").objectReferenceValue = animator;
            tpcSo.ApplyModifiedPropertiesWithoutUndo();

            var camRoot = new GameObject("CameraRoot");
            camRoot.transform.SetParent(root.transform, false);
            camRoot.transform.localPosition = new Vector3(0f, p.CameraRootHeight, 0f);

#if ENABLE_INPUT_SYSTEM
            var pi = root.AddComponent<PlayerInput>();
            pi.actions = p.InputActions;
            pi.defaultActionMap = p.DefaultActionMap;
            pi.notificationBehavior = PlayerNotifications.SendMessages;
#endif

            // Every character lives in the hierarchy; the switcher just toggles them.
            var switcher = root.AddComponent<CharacterSwitcher>();
            var swSo = new SerializedObject(switcher);
            swSo.FindProperty("_animator").objectReferenceValue = animator;
            swSo.ApplyModifiedPropertiesWithoutUndo();

            int defaultIndex = Mathf.Clamp(p.DefaultCharacter, 0, models.Count - 1);
            for (int i = 0; i < models.Count; i++)
            {
                var entry = CreateCharacterChild(root.transform, models[i].Model, models[i].Name, false);
                entry.Root.SetActive(i == defaultIndex);
                switcher.Characters.Add(entry);
            }
            swSo.Update();
            swSo.FindProperty("_activeIndex").intValue = defaultIndex;
            swSo.ApplyModifiedPropertiesWithoutUndo();
            animator.avatar = switcher.Characters[defaultIndex].Avatar;

            EnsureFolder(p.PrefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, p.PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Player prefab built at {p.PrefabPath}", prefab);
            return prefab;
        }

        /// <summary>
        /// Instantiates a model as a child character, strips its own Animator (the Player's
        /// Animator drives it) and returns a filled switcher entry.
        /// </summary>
        public static CharacterSwitcher.CharacterEntry CreateCharacterChild(Transform parent, GameObject model,
            string name, bool undo)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, parent);
            go.name = string.IsNullOrEmpty(name) ? model.name : name;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            if (undo) Undo.RegisterCreatedObjectUndo(go, "Add Character");

            if (go.TryGetComponent<Animator>(out var childAnim))
            {
                if (undo) Undo.DestroyObjectImmediate(childAnim);
                else Object.DestroyImmediate(childAnim);
            }

            var avatar = FindAvatar(model);
            if (avatar == null || !avatar.isHuman)
                Debug.LogWarning($"{Tag} '{go.name}' has no valid Humanoid avatar; it cannot share the animation.", model);

            return new CharacterSwitcher.CharacterEntry { Name = go.name, Root = go, Avatar = avatar };
        }

        public static Avatar FindAvatar(GameObject model)
        {
            string path = AssetDatabase.GetAssetPath(model);
            var avatar = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null && model.TryGetComponent<Animator>(out var a)) avatar = a.avatar;
            return avatar;
        }

        // ---------------------------------------------------------------- Scene

        public static GameObject AddPlayerToOpenScene(CharacterSetupProfile p)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.PrefabPath) ?? BuildPlayerPrefab(p);
            if (prefab == null) return null;

            // Replace an existing rig so re-running stays idempotent.
            foreach (var old in Object.FindObjectsByType<ThirdPersonController>(FindObjectsInactive.Include))
                Undo.DestroyObjectImmediate(old.gameObject);

            if (p.CreateGroundIfMissing && GameObject.Find("Ground") == null)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = p.GroundScale;
                Undo.RegisterCreatedObjectUndo(ground, "Create Ground");
            }

            if (p.CreateSunIfMissing &&
                Object.FindObjectsByType<Light>().All(l => l.type != LightType.Directional))
            {
                var sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                sun.shadows = LightShadows.Soft;
                Undo.RegisterCreatedObjectUndo(sun.gameObject, "Create Sun");
            }

            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            player.transform.position = p.SpawnPosition;
            Undo.RegisterCreatedObjectUndo(player, "Add Player");

            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.gameObject.AddComponent<AudioListener>();
                Undo.RegisterCreatedObjectUndo(cam.gameObject, "Create Camera");
            }

            var tpCam = cam.GetComponent<ThirdPersonCamera>();
            if (tpCam == null) tpCam = Undo.AddComponent<ThirdPersonCamera>(cam.gameObject);
            Undo.RecordObject(tpCam, "Wire Camera");
            tpCam.Target = player.transform.Find("CameraRoot");
            tpCam.Input = player.GetComponent<PlayerInputs>();
            tpCam.HeightOffset = p.CameraLookOffset;
            tpCam.Distance = p.CameraDistance;
            Undo.RecordObject(cam.transform, "Wire Camera");
            cam.transform.rotation = Quaternion.Euler(p.CameraStartPitch, 0f, 0f);

            var tpc = player.GetComponent<ThirdPersonController>();
            Undo.RecordObject(tpc, "Wire Camera");
            tpc.CameraTransform = cam.transform;
            PrefabUtility.RecordPrefabInstancePropertyModifications(tpc);

            EditorSceneManager.MarkSceneDirty(player.scene);
            Selection.activeGameObject = player;
            Debug.Log($"{Tag} Player added to '{player.scene.name}'. Save the scene and press Play.", player);
            return player;
        }

        // ---------------------------------------------------------------- Defaults

        /// <summary>Fills a fresh profile with the Mixamo assets that ship with this project.</summary>
        public static void ApplyProjectDefaults(CharacterSetupProfile p)
        {
            const string anim = "Assets/Art/Animations/XBot/X Bot@";
            p.Characters.Clear();
            AddModel(p, "X Bot", "Assets/Art/Characters/XBot/X Bot.fbx");
            AddModel(p, "Man In Suit", "Assets/Art/Characters/ManInSuit/Man In Suit@T-Pose.fbx");

            // Locomotion comes from the MxM mocap library (our Mixamo locomotion clips were removed).
            p.Idle = MotionMatchingSetup.MocapClip("WalkingMocapSet.fbx", "01_IdleLoop");
            p.Walk = MotionMatchingSetup.MocapClip("WalkingMocapSet.fbx", "24_WalkStraight_Neutral_01");
            p.Run = MotionMatchingSetup.MocapClip("RunningMocapSet.fbx", "24_RunCycle_Neutral_01");
            p.Sprint = MotionMatchingSetup.MocapClip("SprintMocapSet.fbx", "SprintLoop");
            p.CrouchIdle = Clip(anim + "Crouching Idle.fbx");
            p.CrouchWalk = Clip(anim + "Crouch Walk.fbx");
            p.Jump = Clip(anim + "Jump.fbx");
            p.Fall = Clip(anim + "Falling.fbx");
            p.GroundLayers = LayerMask.GetMask("Default");
#if ENABLE_INPUT_SYSTEM
            p.InputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Settings/Input/PlayerControls.inputactions");
#endif
        }

        // ---------------------------------------------------------------- helpers

        private static void AddModel(CharacterSetupProfile p, string name, string path)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model != null) p.Characters.Add(new CharacterSetupProfile.CharacterModel { Name = name, Model = model });
        }

        private static AnimationClip Clip(string fbxPath) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

        private static void AddChild(BlendTree tree, AnimationClip clip, float threshold, string field)
        {
            if (Warn(clip, field) != null) tree.AddChild(clip, threshold);
        }

        private static AnimationClip Warn(AnimationClip clip, string field)
        {
            if (clip == null) Debug.LogWarning($"{Tag} Profile field '{field}' has no clip assigned.");
            return clip;
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, float duration,
            params (string param, bool val)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
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
                if (parameters[i].name != name) continue;
                parameters[i].defaultBool = value;
                controller.parameters = parameters;
                return;
            }
        }

        private static void EnsureFolder(string assetPath) =>
            EnsureDirectory(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));

        private static void EnsureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;
            string parent = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(dir));
        }
    }
}
