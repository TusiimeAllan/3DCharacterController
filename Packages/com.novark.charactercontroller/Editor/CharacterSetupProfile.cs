using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Inspector-editable recipe for the third person character rig. Every value the
    /// builder uses (models, clips, blend thresholds, transitions, collider, output paths)
    /// lives here, and the build buttons sit on this asset's Inspector.
    /// </summary>
    [CreateAssetMenu(menuName = "Character Controller/Setup Profile", fileName = "CharacterSetupProfile")]
    public class CharacterSetupProfile : ScriptableObject
    {
        [Serializable]
        public class CharacterModel
        {
            public string Name;
            [Tooltip("Humanoid model asset (FBX or prefab)")]
            public GameObject Model;
        }

        [Header("Characters")]
        public List<CharacterModel> Characters = new List<CharacterModel>();
        [Tooltip("Character enabled by default on the Player")]
        public int DefaultCharacter;

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
        public float CrouchWalkThreshold = 1.5f;

        [Header("Air")]
        public AnimationClip Jump;
        public AnimationClip Fall;
        [Range(0f, 1f)] public float JumpToFallExitTime = 0.7f;

        [Header("Transition Durations")]
        public float CrouchTransition = 0.15f;
        public float JumpTransition = 0.1f;
        public float JumpToFallTransition = 0.2f;
        public float FallTransition = 0.2f;
        public float LandTransition = 0.15f;

        [Header("Character Controller")]
        public float Height = 1.8f;
        public float Radius = 0.28f;
        [Range(0f, 90f)] public float SlopeLimit = 45f;
        public float StepOffset = 0.3f;
        public LayerMask GroundLayers = 1;

        [Header("Camera")]
        [Tooltip("Height of the CameraRoot socket the camera orbits (head height)")]
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

        [Header("Output")]
        public string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        public string PrefabPath = "Assets/Art/Characters/Player.prefab";

        [Header("Scene Setup")]
        public Vector3 SpawnPosition = new Vector3(0f, 0.1f, 0f);
        public bool CreateGroundIfMissing = true;
        public Vector3 GroundScale = new Vector3(10f, 1f, 10f);
        public bool CreateSunIfMissing = true;
    }
}
