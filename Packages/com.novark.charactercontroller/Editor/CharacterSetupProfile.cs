using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// The character kit recipe: which characters to play, every clip each feature uses, which features to
    /// build, and where generated assets go. The Character Wizard fills it; "Build" turns it into an Animator
    /// Controller and a Player prefab. Features whose clips are missing are skipped with a warning.
    /// </summary>
    [CreateAssetMenu(menuName = "Character Controller/Character Kit Profile", fileName = "CharacterKitProfile")]
    public class CharacterSetupProfile : ScriptableObject
    {
        [Serializable]
        public class CharacterModel
        {
            public string Name;
            [Tooltip("Humanoid model asset (FBX or prefab). The wizard converts and rigs models that are not humanoid yet")]
            public GameObject Model;
        }

        [Serializable]
        public class ParkourSource
        {
            public AnimationClip Clip;
            public ParkourKind Kind;
        }

        [Header("Characters")]
        [Tooltip("Your characters. Leave empty to play the bundled X Bot; when any are set, X Bot is not included")]
        public List<CharacterModel> Characters = new List<CharacterModel>();
        [Tooltip("Character enabled by default on the Player")]
        public int DefaultCharacter;

        [Header("Features")]
        public bool EnableCrouch = true;
        public bool EnableStrafe = true;
        public bool EnableParkour = true;
        public bool EnableClimbing = true;
        public bool EnableSwimming = true;
        public bool EnableSlopeSlide = true;
        public bool EnableActions = true;
        [Tooltip("Getting on bikes, cars and seats (CharacterMount)")]
        public bool EnableMounts = true;
        [Tooltip("Foot grounding, pelvis drop and look-at (Humanoid IK)")]
        public bool EnableGrounding = true;
        [Tooltip("Keeps hands, elbows and body out of obstacles during parkour, ledges and ladders")]
        public bool EnableBodyContact = true;
        [Tooltip("Motion matched locomotion (MxM). Off = the Animator Controller blend trees drive locomotion")]
        public bool EnableMotionMatching = true;

        [Header("Locomotion Blend Tree (Speed)")]
        public AnimationClip Idle;
        public AnimationClip Walk;
        public float WalkThreshold = 2f;
        public AnimationClip Run;
        public float RunThreshold = 4f;
        public AnimationClip Sprint;
        public float SprintThreshold = 6f;

        [Header("Crouch Blend Tree (Speed)")]
        public AnimationClip CrouchIdle;
        public AnimationClip CrouchWalk;
        [Tooltip("Crouch move speed (m/s), for the controller and the blend tree")]
        public float CrouchWalkThreshold = 1.2f;
        [Tooltip("Ground speed of the crouch walk clip at normal playback, so the stride matches the crouch speed")]
        public float CrouchClipSpeed = 1.0f;

        [Header("Air")]
        [Tooltip("Takeoff")]
        public AnimationClip Jump;
        public AnimationClip JumpLoop;
        [Tooltip("Long falls")]
        public AnimationClip Fall;
        public AnimationClip Land;
        [Tooltip("Hard landings")]
        public AnimationClip Roll;
        [Range(0f, 1f)] public float JumpToFallExitTime = 0.7f;

        [Header("Strafe")]
        public AnimationClip StrafeWalkForward;
        public AnimationClip StrafeWalkLeft;
        public AnimationClip StrafeWalkRight;
        public AnimationClip StrafeRunForward;
        public AnimationClip StrafeRunLeft;
        public AnimationClip StrafeRunRight;
        public AnimationClip StrafeTurnLeft;
        public AnimationClip StrafeTurnRight;

        [Header("Ledges and Ladders")]
        public AnimationClip HangIdle;
        [Tooltip("Shimmy to the right; mirrored for the left")]
        public AnimationClip HangShimmy;
        public AnimationClip LadderClimb;

        [Header("Swimming")]
        public AnimationClip SwimIdle;
        public AnimationClip SwimForward;

        [Header("Steep Slopes")]
        [Tooltip("Held at its lowest pose while sliding down slopes over the slope limit")]
        public AnimationClip SlopeSlide;
        [Range(0f, 1f)] public float SlopeSlidePoseTime = 0.42f;

        [Header("Actions")]
        public AnimationClip DeathA;
        public AnimationClip DeathB;
        public AnimationClip SitEnter;
        public AnimationClip SitIdle;
        public AnimationClip SitExit;
        public AnimationClip Interact;
        public AnimationClip PickUp;
        public AnimationClip Jab;
        public AnimationClip Cross;
        public AnimationClip Repair;
        public AnimationClip Dance;
        public AnimationClip Talk;
        public AnimationClip Push;
        public AnimationClip HitChest;
        public AnimationClip HitHead;

        [Header("Mounts (bikes, cars, seats)")]
        [Tooltip("Pose while on a CharacterMount; hands and feet reach for the mount's targets on top of it")]
        public AnimationClip MountIdle;

        [Header("Parkour (root motion clips, analysed into a library)")]
        public List<ParkourSource> ParkourClips = new List<ParkourSource>();

        [Header("Motion Matching (MxM)")]
        [Tooltip("MxMAnimData asset")]
        public ScriptableObject MotionMatchingData;
        [Tooltip("MxMInputProfile assets")]
        public ScriptableObject WalkInputProfile;
        public ScriptableObject SprintInputProfile;
        public ScriptableObject StrafeInputProfile;
        [Tooltip("Prefab with tuned MxMAnimator and MxMTrajectoryGenerator settings to start from")]
        public GameObject MotionMatchingTemplate;

        [Header("Transition Durations")]
        public float CrouchTransition = 0.15f;
        public float JumpTransition = 0.1f;
        public float JumpToFallTransition = 0.2f;
        public float FallTransition = 0.2f;
        public float LandTransition = 0.15f;

        [Header("Character Controller (reference body, scaled to each character at runtime)")]
        public float Height = 1.8f;
        public float Radius = 0.28f;
        [Range(0f, 90f)] public float SlopeLimit = 45f;
        public float StepOffset = 0.3f;
        public LayerMask GroundLayers = 1;

        [Header("Camera")]
        [Tooltip("Height of the CameraRoot socket the camera orbits (head height of the reference body)")]
        public float CameraRootHeight = 1.6f;
        [Tooltip("Extra look height above CameraRoot")]
        public float CameraLookOffset = 0f;
        public float CameraDistance = 4f;
        [Tooltip("Starting pitch in degrees")]
        public float CameraStartPitch = 15f;

#if ENABLE_INPUT_SYSTEM
        [Header("Input")]
        public InputActionAsset InputActions;
        public string DefaultActionMap = "Player";
#endif

        [Header("Output (generated assets, inside Assets)")]
        public string ControllerPath = "Assets/CharacterKit/PlayerAnimator.controller";
        public string PrefabPath = "Assets/CharacterKit/Player.prefab";
        public string ParkourLibraryPath = "Assets/CharacterKit/ParkourLibrary.asset";
        public string UpperBodyMaskPath = "Assets/CharacterKit/UpperBody.mask";
        public string SplashMaterialPath = "Assets/CharacterKit/WaterSplash.mat";
        [Tooltip("Where the wizard saves characters it rigs")]
        public string RiggedCharactersFolder = "Assets/CharacterKit/Characters";

        [Header("Scene Setup")]
        public Vector3 SpawnPosition = new Vector3(0f, 0.1f, 0f);
        public bool CreateGroundIfMissing = true;
        public Vector3 GroundScale = new Vector3(10f, 1f, 10f);
        public bool CreateSunIfMissing = true;

        /// <summary>The characters the Player gets: yours, or the bundled X Bot when there are none.</summary>
        public List<CharacterModel> PlayableCharacters()
        {
            var list = Characters.FindAll(c => c != null && c.Model != null);
            if (list.Count == 0)
            {
                var xbot = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(KitPaths.XBot);
                if (xbot != null) list.Add(new CharacterModel { Name = "X Bot", Model = xbot });
            }
            return list;
        }
    }
}
