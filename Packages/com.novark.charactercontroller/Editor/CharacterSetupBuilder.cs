using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using static CharacterControllerSystem.EditorTools.AnimatorBuildUtil;
using Object = UnityEngine.Object;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>Small editor helpers shared by the builders.</summary>
    public static class Kit
    {
        /// <summary>Record changes for undo (scene objects); off while editing prefab contents.</summary>
        public static bool UseUndo;

        public static T Ensure<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) return c;
            return UseUndo ? Undo.AddComponent<T>(go) : go.AddComponent<T>();
        }
    }

    /// <summary>
    /// Optional parts of the kit (motion matching) hook into the Player setup through this, so the core
    /// builder does not depend on them.
    /// </summary>
    public static class CharacterKitModules
    {
        /// <summary>Called after the core components are on the Player; return a short description of what was added.</summary>
        public delegate string PlayerModule(GameObject player, CharacterSetupProfile profile, AnimatorController controller);

        /// <summary>Called on every Animator Controller build, after the kit's states: add your own states here.</summary>
        public delegate void AnimatorModule(AnimatorController controller, CharacterSetupProfile profile);

        private static readonly List<PlayerModule> Modules = new List<PlayerModule>();
        private static readonly List<AnimatorModule> AnimatorModules = new List<AnimatorModule>();

        public static void Register(PlayerModule module)
        {
            if (!Modules.Contains(module)) Modules.Add(module);
        }

        public static void Register(AnimatorModule module)
        {
            if (!AnimatorModules.Contains(module)) AnimatorModules.Add(module);
        }

        internal static IEnumerable<PlayerModule> All => Modules;
        internal static IEnumerable<AnimatorModule> AllAnimator => AnimatorModules;
    }

    /// <summary>
    /// Builds the Animator Controller, Player prefab and scene rig from a <see cref="CharacterSetupProfile"/>.
    /// Every feature reads its clips from the profile and is skipped (with a warning) when they are missing.
    /// </summary>
    public static class CharacterSetupBuilder
    {
        private const string Tag = "[CharacterKit]";

        /// <summary>Every parameter any component writes, so none of them ever logs "parameter does not exist".</summary>
        private static readonly (string name, AnimatorControllerParameterType type)[] Parameters =
        {
            ("Speed", AnimatorControllerParameterType.Float), ("MotionSpeed", AnimatorControllerParameterType.Float),
            ("Grounded", AnimatorControllerParameterType.Bool), ("Jump", AnimatorControllerParameterType.Bool),
            ("FreeFall", AnimatorControllerParameterType.Bool), ("Crouch", AnimatorControllerParameterType.Bool),
            ("FallSpeed", AnimatorControllerParameterType.Float), ("Strafing", AnimatorControllerParameterType.Bool),
            ("StrafeX", AnimatorControllerParameterType.Float), ("StrafeZ", AnimatorControllerParameterType.Float),
            ("StrafeTurn", AnimatorControllerParameterType.Int), ("SlopeSlide", AnimatorControllerParameterType.Bool),
            ("Parkour", AnimatorControllerParameterType.Int), ("Hanging", AnimatorControllerParameterType.Bool),
            ("HangMove", AnimatorControllerParameterType.Float), ("OnLadder", AnimatorControllerParameterType.Bool),
            ("LadderSpeed", AnimatorControllerParameterType.Float), ("Swimming", AnimatorControllerParameterType.Bool),
            ("SwimSpeed", AnimatorControllerParameterType.Float), ("Action", AnimatorControllerParameterType.Int),
            ("Dead", AnimatorControllerParameterType.Bool), ("DeathIndex", AnimatorControllerParameterType.Int),
            ("Sitting", AnimatorControllerParameterType.Bool), ("Pushing", AnimatorControllerParameterType.Bool),
            ("Hit", AnimatorControllerParameterType.Trigger), ("HitIndex", AnimatorControllerParameterType.Int),
            ("Mounted", AnimatorControllerParameterType.Bool), ("MountPose", AnimatorControllerParameterType.Int),
        };

        // ---------------------------------------------------------------- Animator

        public static AnimatorController BuildAnimator(CharacterSetupProfile p)
        {
            var controller = ResetController(p.ControllerPath);

            foreach (var (name, type) in Parameters) Param(controller, name, type);
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

            var jumpState = sm.AddState("Jump");
            jumpState.motion = Warn(p.Jump, nameof(p.Jump));
            var fallState = sm.AddState("Fall");
            fallState.motion = Warn(p.Fall, nameof(p.Fall));

            sm.defaultState = locoState;

            if (p.EnableCrouch)
            {
                AddTransition(locoState, crouchState, p.CrouchTransition, ("Crouch", true));
                AddTransition(crouchState, locoState, p.CrouchTransition, ("Crouch", false));
            }
            AddTransition(locoState, jumpState, p.JumpTransition, ("Jump", true));

            var jumpToFall = jumpState.AddTransition(fallState);
            jumpToFall.hasExitTime = true;
            jumpToFall.exitTime = p.JumpToFallExitTime;
            jumpToFall.duration = p.JumpToFallTransition;

            AddTransition(locoState, fallState, p.FallTransition, ("FreeFall", true));
            AddTransition(fallState, locoState, p.LandTransition, ("Grounded", true));
            AddTransition(jumpState, locoState, p.LandTransition, ("Grounded", true));

            var built = new List<string> { "locomotion", "jump", "fall" };
            void Feature(bool enabled, string name, Func<bool> build)
            {
                if (enabled && build()) built.Add(name);
            }
            Feature(true, "landings", () => AirborneStatesBuilder.Apply(controller, p));
            Feature(p.EnableCrouch, "crouch", () => CrouchStatesBuilder.Apply(controller, p));
            Feature(p.EnableActions, "actions", () => ActionStatesBuilder.Apply(controller, p));
            Feature(p.EnableStrafe, "strafe", () => StrafeStatesBuilder.Apply(controller, p));
            Feature(p.EnableParkour, "parkour", () => ParkourBuilder.ApplyAnimator(controller, ParkourBuilder.BuildLibrary(p)));
            Feature(p.EnableSlopeSlide, "slope slide", () => SlopeStatesBuilder.Apply(controller, p));
            Feature(p.EnableClimbing, "ledges and ladders", () => ClimbStatesBuilder.Apply(controller, p));
            Feature(p.EnableSwimming, "swimming", () => SwimStatesBuilder.Apply(controller, p));
            Feature(p.EnableMounts, "mounts", () => MountStatesBuilder.Apply(controller, p));
            foreach (var module in CharacterKitModules.AllAnimator) module(controller, p);
            StrafeStatesBuilder.SyncExits(controller); // strafe reaches everything added after it

            // Foot grounding and the body IK run in the IK pass of every layer.
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++) layers[i].iKPass = true;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Animator built at {p.ControllerPath}: {string.Join(", ", built)}.", controller);
            return controller;
        }

        /// <summary>
        /// An empty controller at the path. An existing one is emptied in place, so its GUID (and every prefab
        /// or scene that uses it) stays valid.
        /// </summary>
        private static AnimatorController ResetController(string path)
        {
            EnsureFolderFor(path);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null) return AnimatorController.CreateAnimatorControllerAtPath(path);

            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                if (sub != null && sub != controller && !(sub is AnimatorController)) Object.DestroyImmediate(sub, true);
            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            controller.layers = Array.Empty<AnimatorControllerLayer>();
            controller.AddLayer("Base Layer");
            return controller;
        }

        // ---------------------------------------------------------------- Prefab

        public static GameObject BuildPlayerPrefab(CharacterSetupProfile p)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(p.ControllerPath) ?? BuildAnimator(p);

            var models = p.PlayableCharacters();
            if (models.Count == 0)
            {
                Debug.LogError($"{Tag} No character to build: add one in the wizard (the bundled X Bot is missing too).", p);
                return null;
            }

            Kit.UseUndo = false;
            // An existing Player is updated in place: every component keeps its identity and tuning, so scenes
            // that use it (and anything added to their instances) stay connected.
            bool update = AssetDatabase.LoadAssetAtPath<GameObject>(p.PrefabPath) != null;
            var root = update ? PrefabUtility.LoadPrefabContents(p.PrefabPath) : new GameObject("Player");
            try
            {
                if (!update)
                {
                    var cc = root.AddComponent<CharacterController>();
                    cc.center = new Vector3(0f, p.Height * 0.5f, 0f);
                    cc.height = p.Height;
                    cc.radius = p.Radius;
                    cc.slopeLimit = p.SlopeLimit;
                    cc.stepOffset = p.StepOffset;
                }
                var animator = Kit.Ensure<Animator>(root);
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;

                SyncCharacters(root, animator, models, p.DefaultCharacter);
                ConfigurePlayer(root, p, controller);

                EnsureFolderFor(p.PrefabPath);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, p.PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"{Tag} Player prefab {(update ? "updated" : "built")} at {p.PrefabPath} with {string.Join(", ", models.Select(m => m.Name))}.", prefab);
                return prefab;
            }
            finally
            {
                if (update) PrefabUtility.UnloadPrefabContents(root);
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Makes the switcher hold exactly these characters: keeps the ones already there (and their
        /// placement), adds new ones, removes the ones no longer listed (so X Bot disappears once you use
        /// your own characters).
        /// </summary>
        private static void SyncCharacters(GameObject root, Animator animator,
            List<CharacterSetupProfile.CharacterModel> models, int defaultCharacter)
        {
            var switcher = Kit.Ensure<CharacterSwitcher>(root);
            var swSo = new SerializedObject(switcher);
            swSo.FindProperty("_animator").objectReferenceValue = animator;
            swSo.ApplyModifiedPropertiesWithoutUndo();

            GameObject SourceOf(CharacterSwitcher.CharacterEntry e) =>
                e?.Root != null ? PrefabUtility.GetCorrespondingObjectFromSource(e.Root) : null;

            var kept = new List<CharacterSwitcher.CharacterEntry>();
            foreach (var m in models)
            {
                var existing = switcher.Characters.FirstOrDefault(e => SourceOf(e) == m.Model && !kept.Contains(e));
                if (existing != null)
                {
                    existing.Name = string.IsNullOrEmpty(m.Name) ? existing.Name : m.Name;
                    existing.Avatar = FindAvatar(m.Model);
                    kept.Add(existing);
                }
                else kept.Add(CreateCharacterChild(root.transform, m.Model, m.Name, false));
            }
            foreach (var old in switcher.Characters.Where(e => !kept.Contains(e)).ToList())
                if (old?.Root != null) Object.DestroyImmediate(old.Root);
            switcher.Characters = kept;

            int index = Mathf.Clamp(defaultCharacter, 0, kept.Count - 1);
            for (int i = 0; i < kept.Count; i++) kept[i].Root.SetActive(i == index);
            swSo.Update();
            swSo.FindProperty("_activeIndex").intValue = index;
            swSo.ApplyModifiedPropertiesWithoutUndo();
            animator.avatar = kept[index].Avatar;
        }

        /// <summary>
        /// Adds and wires every component the controller's features need. Works on prefab contents and on a
        /// model already in a scene (Make Playable). Components for features the controller lacks are not added.
        /// </summary>
        public static void ConfigurePlayer(GameObject root, CharacterSetupProfile p, AnimatorController controller)
        {
            var cc = Kit.Ensure<CharacterController>(root);
            var animator = Kit.Ensure<Animator>(root);
            if (controller != null) animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            var inputs = Kit.Ensure<PlayerInputs>(root);
            var tpc = Kit.Ensure<ThirdPersonController>(root);
            tpc.GroundLayers = p.GroundLayers;
            tpc.StandingHeight = p.Height;
            tpc.GroundedRadius = p.Radius;
            var tpcSo = new SerializedObject(tpc);
            tpcSo.FindProperty("_controller").objectReferenceValue = cc;
            tpcSo.FindProperty("_input").objectReferenceValue = inputs;
            tpcSo.FindProperty("_animator").objectReferenceValue = animator;
            tpcSo.ApplyModifiedPropertiesWithoutUndo();

            if (root.transform.Find("CameraRoot") == null)
            {
                var camRoot = new GameObject("CameraRoot");
                if (Kit.UseUndo) Undo.RegisterCreatedObjectUndo(camRoot, "Make Playable");
                camRoot.transform.SetParent(root.transform, false);
                camRoot.transform.localPosition = new Vector3(0f, p.CameraRootHeight, 0f);
            }

#if ENABLE_INPUT_SYSTEM
            var pi = Kit.Ensure<PlayerInput>(root);
            pi.actions = p.InputActions;
            pi.defaultActionMap = p.DefaultActionMap;
            pi.notificationBehavior = PlayerNotifications.SendMessages;
#endif
            Kit.Ensure<CharacterDimensions>(root);

            bool Has(string state) => State(controller, state) != null;
            if (p.EnableCrouch) CrouchStatesBuilder.SetUpPlayer(root, p);
            if (p.EnableActions && Has("Interact")) ActionStatesBuilder.SetUpPlayer(root, p);
            if (p.EnableParkour)
            {
                var library = AssetDatabase.LoadAssetAtPath<ParkourLibrary>(p.ParkourLibraryPath);
                if (library != null && library.Moves.Count > 0) ParkourBuilder.SetUpPlayer(root, library);
            }
            if (p.EnableClimbing && Has("Hang Idle")) ClimbStatesBuilder.SetUpPlayer(root, p);
            if (p.EnableSwimming && Has("Swim")) SwimStatesBuilder.SetUpPlayer(root, p);
            if (p.EnableBodyContact)
            {
                var solver = Kit.Ensure<BodyContactSolver>(root);
                solver.Controller = tpc;
                solver.Parkour = root.GetComponent<ParkourController>();
            }
            if (p.EnableGrounding)
            {
                var grounding = Kit.Ensure<HumanoidGrounding>(root);
                grounding.Controller = tpc;
                grounding.GroundLayers = p.GroundLayers;
            }
            if (p.EnableMounts && Has("Mounted")) Kit.Ensure<CharacterMountController>(root);
            Kit.Ensure<CharacterSockets>(root);
            Kit.Ensure<CharacterControllerRig>(root);

            foreach (var module in CharacterKitModules.All)
            {
                string added = module(root, p, controller);
                if (!string.IsNullOrEmpty(added)) Debug.Log($"{Tag} {added}");
            }
            CharacterControllerRig.HideParts(root.GetComponent<CharacterControllerRig>(), true);
        }

        /// <summary>
        /// Instantiates a model as a child character, strips its own Animator (the Player's Animator drives it)
        /// and returns a filled switcher entry.
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
                Debug.LogWarning($"{Tag} '{go.name}' has no valid Humanoid avatar; run it through the Character Wizard.", model);

            return new CharacterSwitcher.CharacterEntry { Name = go.name, Root = go, Avatar = avatar };
        }

        public static Avatar FindAvatar(GameObject model)
        {
            string path = AssetDatabase.GetAssetPath(model);
            var avatar = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault(a => a.isHuman) ??
                  AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if ((avatar == null || !avatar.isHuman) && model.TryGetComponent<Animator>(out var a) && a.avatar != null) avatar = a.avatar;
            if ((avatar == null || !avatar.isHuman) && !string.IsNullOrEmpty(path))
            {
                // A prefab built on a model file: the avatar lives on the model.
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(model);
                if (source != null && source != model)
                {
                    var sourceAvatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(source)).OfType<Avatar>().FirstOrDefault(x => x.isHuman);
                    if (sourceAvatar != null) avatar = sourceAvatar;
                }
            }
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

            if (p.CreateGroundIfMissing && !Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude).Any())
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = p.GroundScale;
                Undo.RegisterCreatedObjectUndo(ground, "Create Ground");
            }

            if (p.CreateSunIfMissing &&
                Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude).All(l => l.type != LightType.Directional))
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
            WireCamera(player, p);
            Selection.activeGameObject = player;
            Debug.Log($"{Tag} Player added to '{player.scene.name}'. Save the scene and press Play.", player);
            return player;
        }

        /// <summary>Puts a ThirdPersonCamera on the main camera (creating one if needed) and points it at the player.</summary>
        public static void WireCamera(GameObject player, CharacterSetupProfile p)
        {
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
            if (PrefabUtility.IsPartOfPrefabInstance(tpc)) PrefabUtility.RecordPrefabInstancePropertyModifications(tpc);
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(player.scene);
        }

        // ---------------------------------------------------------------- Defaults

        /// <summary>
        /// Fills the profile with the clips, input and motion matching data bundled in the package.
        /// Fields already set are kept unless overwrite is true. Characters are left alone.
        /// </summary>
        public static void ApplyKitDefaults(CharacterSetupProfile p, bool overwrite)
        {
            void Set<T>(ref T field, T value) where T : Object
            {
                if (value != null && (overwrite || field == null)) field = value;
            }
            AnimationClip U(string n) => KitPaths.UalClip(n);
            AnimationClip M(string n) => KitPaths.MixamoClip(n);

            Set(ref p.Idle, KitPaths.MocapClip("WalkingMocapSet.fbx", "01_IdleLoop"));
            Set(ref p.Walk, KitPaths.MocapClip("WalkingMocapSet.fbx", "24_WalkStraight_Neutral_01"));
            Set(ref p.Run, KitPaths.MocapClip("RunningMocapSet.fbx", "24_RunCycle_Neutral_01"));
            Set(ref p.Sprint, KitPaths.MocapClip("SprintMocapSet.fbx", "SprintLoop"));
            Set(ref p.CrouchIdle, U("Crouch_Idle_Loop"));
            Set(ref p.CrouchWalk, U("Crouch_Fwd_Loop"));
            Set(ref p.Jump, U("Jump_Start"));
            Set(ref p.JumpLoop, U("Jump_Loop"));
            Set(ref p.Fall, M("Falling"));
            Set(ref p.Land, U("Jump_Land"));
            Set(ref p.Roll, U("Roll"));
            Set(ref p.StrafeWalkForward, M("Strafe Walk Forward"));
            Set(ref p.StrafeWalkLeft, M("Strafe Walk Left"));
            Set(ref p.StrafeWalkRight, M("Strafe Walk Right"));
            Set(ref p.StrafeRunForward, M("Strafe Run Forward"));
            Set(ref p.StrafeRunLeft, M("Strafe Run Left"));
            Set(ref p.StrafeRunRight, M("Strafe Run Right"));
            Set(ref p.StrafeTurnLeft, M("Strafe Turn Left 90"));
            Set(ref p.StrafeTurnRight, M("Strafe Turn Right 90"));
            Set(ref p.HangIdle, M("Hanging Idle"));
            Set(ref p.HangShimmy, M("Braced Hang Shimmy"));
            Set(ref p.LadderClimb, M("Climbing Ladder"));
            Set(ref p.SwimIdle, U("Swim_Idle_Loop"));
            Set(ref p.SwimForward, U("Swim_Fwd_Loop"));
            Set(ref p.SlopeSlide, KitPaths.EventClip("RunSlide_ToRight_1.anim"));
            Set(ref p.DeathA, U("Death01"));
            Set(ref p.DeathB, M("Dying Backwards"));
            Set(ref p.SitEnter, U("Sitting_Enter"));
            Set(ref p.SitIdle, U("Sitting_Idle_Loop"));
            Set(ref p.SitExit, U("Sitting_Exit"));
            Set(ref p.Interact, U("Interact"));
            Set(ref p.PickUp, U("PickUp_Table"));
            Set(ref p.Jab, U("Punch_Jab"));
            Set(ref p.Cross, U("Punch_Cross"));
            Set(ref p.Repair, U("Fixing_Kneeling"));
            Set(ref p.Dance, U("Dance_Loop"));
            Set(ref p.Talk, U("Idle_Talking_Loop"));
            Set(ref p.Push, U("Push_Loop"));
            Set(ref p.HitChest, U("Hit_Chest"));
            Set(ref p.HitHead, U("Hit_Head"));
            Set(ref p.MountIdle, U("Sitting_Idle_Loop"));
            if (overwrite || p.ParkourClips.Count == 0) p.ParkourClips = ParkourBuilder.DefaultSources();

            Set(ref p.MotionMatchingData, AssetDatabase.LoadAssetAtPath<ScriptableObject>(KitPaths.MxmAnimData));
            Set(ref p.WalkInputProfile, AssetDatabase.LoadAssetAtPath<ScriptableObject>(KitPaths.MxmProfiles + "MocapInputProfile_Balanced.asset"));
            Set(ref p.SprintInputProfile, AssetDatabase.LoadAssetAtPath<ScriptableObject>(KitPaths.MxmProfiles + "MocapSprintInputProfile_Balanced.asset"));
            Set(ref p.StrafeInputProfile, AssetDatabase.LoadAssetAtPath<ScriptableObject>(KitPaths.MxmProfiles + "MocapStrafeInputProfile_Balanced.asset"));
            Set(ref p.MotionMatchingTemplate, AssetDatabase.LoadAssetAtPath<GameObject>(KitPaths.MxmDemoCharacter));
#if ENABLE_INPUT_SYSTEM
            Set(ref p.InputActions, AssetDatabase.LoadAssetAtPath<InputActionAsset>(KitPaths.InputActions));
#endif
            if (overwrite) p.GroundLayers = LayerMask.GetMask("Default");
            EditorUtility.SetDirty(p);
        }

        // ---------------------------------------------------------------- helpers

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
    }
}
