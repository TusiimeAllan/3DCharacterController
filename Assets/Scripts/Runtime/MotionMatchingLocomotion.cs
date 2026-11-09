using System.Reflection;
using MxM;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Bridges ThirdPersonController to MxM motion matching while the controller stays in charge of
    /// movement: the CharacterController still moves and turns the capsule (MxM root motion is off),
    /// MxM only chooses the poses that best match where the controller is heading.
    ///
    /// Ground locomotion (idle, walk, run, sprint, starts, stops, turns) comes from MxM. Jump, fall,
    /// crouch and strafing, which the MxM data has no clips for, come from our Animator Controller, hosted by MxM as
    /// its controller layer and faded in whenever the character is airborne, crouched or strafing.
    /// Press M in play mode to compare against the plain Animator Controller.
    /// </summary>
    [DefaultExecutionOrder(100)] // after ThirdPersonController has updated this frame's state
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MxMAnimator))]
    public class MotionMatchingLocomotion : MonoBehaviour
    {
        [Tooltip("Off = the plain Animator Controller drives everything, for A/B comparison")]
        public bool UseMotionMatching = true;
#if ENABLE_INPUT_SYSTEM
        public Key ToggleKey = Key.M;
#endif

        [Header("References")]
        public ThirdPersonController Controller;
        public PlayerInputs Inputs;
        public MxMAnimator Mxm;
        public MxMTrajectoryGenerator Trajectory;

        [Header("Locomotion")]
        public MxMInputProfile WalkProfile;
        public MxMInputProfile SprintProfile;
        public string GeneralCalibration = "General";
        public string SprintCalibration = "Sprint";
        [Tooltip("Trajectory responsiveness while walking / running")]
        public float WalkBias = 10f;
        [Tooltip("Trajectory responsiveness while sprinting (lower = wider, smoother turns)")]
        public float SprintBias = 6f;

        [Header("Strafing")]
        [Tooltip("Strafe with the motion capture strafe set (tag \"Strafe\") instead of the Animator Controller blend tree")]
        public bool MotionMatchedStrafe = true;
        public MxMInputProfile StrafeProfile;
        public string StrafeTag = "Strafe";
        public string StrafeCalibration = "Strafe";

        [Header("Controller Layer (jump, fall, crouch)")]
        [Tooltip("Seconds to fade our Animator Controller in when leaving the ground or crouching")]
        public float LayerFadeIn = 0.1f;
        [Tooltip("Seconds to hand back to motion matching after landing or standing up")]
        public float LayerFadeOut = 0.25f;

        public event System.Action<bool> ModeChanged;

        private static readonly FieldInfo ControllerPlayableField =
            typeof(MxMAnimator).GetField("m_animControllerPlayable", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");
        private static readonly int JumpId = Animator.StringToHash("Jump");
        private static readonly int FreeFallId = Animator.StringToHash("FreeFall");
        private static readonly int CrouchId = Animator.StringToHash("Crouch");
        private static readonly int FallSpeedId = Animator.StringToHash("FallSpeed");
        private static readonly int StrafingId = Animator.StringToHash("Strafing");
        private static readonly int StrafeXId = Animator.StringToHash("StrafeX");
        private static readonly int StrafeZId = Animator.StringToHash("StrafeZ");
        private static readonly int StrafeTurnId = Animator.StringToHash("StrafeTurn");
        private static readonly int SlopeSlideId = Animator.StringToHash("SlopeSlide");
        private const string LandingTag = "Landing";

        /// <summary>True while the Animator Controller plays a landing or roll (states tagged "Landing").</summary>
        public bool InLandingState { get; private set; }

        private AnimatorControllerPlayable _controllerLayer;
        private CharacterActions _actions;
        private float _layerWeight = 1f;
        private bool _sprinting;
        private bool _calibrationReady;
        private EAngularErrorWarp _angularWarp;
        private bool _mxmStrafing;

        private void Reset() => AutoAssign();

        private void AutoAssign()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Inputs == null) Inputs = GetComponent<PlayerInputs>();
            if (Mxm == null) Mxm = GetComponent<MxMAnimator>();
            if (Trajectory == null) Trajectory = GetComponent<MxMTrajectoryGenerator>();
        }

        private void Awake()
        {
            AutoAssign();
            if (Controller == null || Inputs == null || Mxm == null || Trajectory == null)
            {
                Debug.LogError($"[{nameof(MotionMatchingLocomotion)}] Missing references, disabling.", this);
                enabled = false;
                return;
            }

            EnsureSearchManager();
            _actions = GetComponent<CharacterActions>();

            // We drive the animation from here, the controller only publishes its state.
            Controller.DriveAnimator = false;
            var switcher = GetComponent<CharacterSwitcher>();
            if (switcher != null) switcher.PreserveAnimatorState = false; // MxM keeps its own pose across swaps
        }

        private void Start()
        {
            if (Trajectory.RelativeCameraTransform == null) Trajectory.RelativeCameraTransform = Controller.CameraTransform;
            _angularWarp = Mxm.AngularErrorWarpType;
            ApplyGait(false, force: true);
        }

        private void OnDisable()
        {
            if (Controller != null) Controller.DriveAnimator = true;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current[ToggleKey].wasPressedThisFrame) SetMode(!UseMotionMatching);
#endif
            // Trajectory: same input and camera the controller moves with, at the speed it is moving.
            Vector2 move = Controller.MovementLocked || Controller.ExternallyDriven ? Vector2.zero : Inputs.move;
            Trajectory.SetInput(move);
            bool sprinting = Controller.Grounded && Inputs.sprint && !Controller.IsCrouching && move != Vector2.zero;
            UpdateStrafeMode(Controller.IsStrafing && MotionMatchedStrafe && UseMotionMatching);
            if (!_mxmStrafing && sprinting != _sprinting) ApplyGait(sprinting);
            float maxSpeed = _mxmStrafing
                ? (Inputs.sprint ? Controller.StrafeSprintSpeed : Controller.MoveSpeed)
                : (sprinting ? Controller.SprintSpeed : Controller.MoveSpeed);
            Trajectory.MaxSpeed = maxSpeed * Controller.SpeedScale * Controller.SlopeSpeedFactor * Controller.WadeSpeedFactor;

            // MxM's angular error warping turns the body towards the travel direction. While strafing the
            // controller keeps it facing the camera instead, so the two would fight.
            var warp = Controller.IsStrafing ? EAngularErrorWarp.Off : _angularWarp;
            if (Mxm.AngularErrorWarpType != warp) Mxm.AngularErrorWarpType = warp;

            if (!ResolveControllerLayer()) return;

            // Hand jump, fall, crouch and strafing to our Animator Controller; ground locomotion to MxM.
            // (The MxM mocap has no sideways or backwards clips, so strafing uses the Strafe blend tree.)
            // Landings and rolls finish on our controller before handing back, so they are not cut off.
            InLandingState = _controllerLayer.GetCurrentAnimatorStateInfo(0).IsTag(LandingTag) ||
                             (_controllerLayer.IsInTransition(0) &&
                              _controllerLayer.GetNextAnimatorStateInfo(0).IsTag(LandingTag));
            // Strafing stays on motion matching (the strafe set) except for standing turns, which use the turn clips.
            bool strafeOnController = Controller.IsStrafing && (!_mxmStrafing || Controller.StrafeTurn != 0);
            bool useController = !UseMotionMatching || !Controller.Grounded || Controller.IsJumping ||
                                 Controller.IsCrouching || strafeOnController || Controller.ExternallyDriven || Controller.IsSlidingOnSlope || InLandingState ||
                                 (_actions != null && _actions.isActiveAndEnabled && _actions.NeedsControllerLayer);
            float target = useController ? 1f : 0f;
            float duration = target > _layerWeight ? LayerFadeIn : LayerFadeOut;
            _layerWeight = duration <= 0f ? target : Mathf.MoveTowards(_layerWeight, target, Time.deltaTime / duration);
            Mxm.SetControllerLayerWeight(_layerWeight);
            MatchPlaybackToSpeed(useController);

            _controllerLayer.SetFloat(SpeedId, Controller.AnimationBlend);
            _controllerLayer.SetFloat(MotionSpeedId, Controller.InputMagnitude);
            _controllerLayer.SetBool(GroundedId, Controller.Grounded);
            _controllerLayer.SetBool(JumpId, Controller.IsJumping);
            _controllerLayer.SetBool(FreeFallId, Controller.IsFreeFalling);
            _controllerLayer.SetBool(CrouchId, Controller.IsCrouching);
            _controllerLayer.SetFloat(FallSpeedId, Controller.FallSpeed);
            _controllerLayer.SetBool(StrafingId, Controller.IsStrafing);
            _controllerLayer.SetFloat(StrafeXId, Controller.LocalVelocity.x);
            _controllerLayer.SetFloat(StrafeZId, Controller.LocalVelocity.y);
            _controllerLayer.SetInteger(StrafeTurnId, Controller.StrafeTurn);
            _controllerLayer.SetBool(SlopeSlideId, Controller.IsSlidingOnSlope);
        }

        [Tooltip("Plays motion matched clips faster or slower so the feet keep pace with the body (min, max)")]
        public Vector2 StrideMatchLimits = new Vector2(0.8f, 1.4f);
        private float _strideScale = 1f;
        private CharacterController _body;

        /// <summary>
        /// The controller sets the speed, not the recording, so a 1.5 m/s walk clip under a 2 m/s body slides its
        /// planted feet. Scaling the playback by body speed over the pose's recorded speed keeps the stride in step.
        /// </summary>
        private void MatchPlaybackToSpeed(bool onController)
        {
            if (_body == null) _body = GetComponent<CharacterController>();
            float target = 1f;
            if (!onController && _body != null && Controller.Grounded)
            {
                Vector3 recorded = Mxm.CurrentInterpolatedPose.LocalVelocity;
                recorded.y = 0f;
                Vector3 actual = _body.velocity;
                actual.y = 0f;
                if (recorded.magnitude > 0.4f && actual.magnitude > 0.3f)
                    target = Mathf.Clamp(actual.magnitude / recorded.magnitude, StrideMatchLimits.x, StrideMatchLimits.y);
            }
            _strideScale = Mathf.MoveTowards(_strideScale, target, 2f * Time.deltaTime);
            Mxm.UserPlaybackSpeedMultiplier = _strideScale;
        }

        public void SetMode(bool useMotionMatching)
        {
            if (UseMotionMatching == useMotionMatching) return;
            UseMotionMatching = useMotionMatching;
            ModeChanged?.Invoke(useMotionMatching);
        }

        /// <summary>
        /// Strafe set on/off (as in the MxM demo): only poses tagged Strafe, the strafe calibration and input
        /// profile, and a trajectory that keeps facing the camera while it follows the stick.
        /// </summary>
        private void UpdateStrafeMode(bool on)
        {
            if (on == _mxmStrafing || Mxm.CurrentAnimData == null) return;
            _mxmStrafing = on;
            if (on)
            {
                Mxm.AddRequiredTag(StrafeTag);
                Mxm.SetCalibrationData(StrafeCalibration);
                Mxm.SetFavourCurrentPose(true, 0.95f);
                Trajectory.TrajectoryMode = ETrajectoryMoveMode.Strafe;
                if (StrafeProfile != null) Trajectory.InputProfile = StrafeProfile;
                Trajectory.PositionBias = Trajectory.DirectionBias = WalkBias;
                Mxm.PastTrajectoryMode = EPastTrajectoryMode.CopyFromCurrentPose;
            }
            else
            {
                Mxm.RemoveRequiredTag(StrafeTag);
                Mxm.SetFavourCurrentPose(false, 1f);
                Trajectory.TrajectoryMode = ETrajectoryMoveMode.Normal;
                Mxm.PastTrajectoryMode = EPastTrajectoryMode.ActualHistory;
                ApplyGait(_sprinting, force: true); // back to the walk/sprint profile and calibration
            }
        }

        private void ApplyGait(bool sprinting, bool force = false)
        {
            if (!force && sprinting == _sprinting) return;
            _sprinting = sprinting;

            var profile = sprinting ? SprintProfile : WalkProfile;
            if (profile != null) Trajectory.InputProfile = profile;
            Trajectory.PositionBias = Trajectory.DirectionBias = sprinting ? SprintBias : WalkBias;

            // Calibration sets weight the pose search for the gait (they exist in the demo MocapAnimData).
            if (!_calibrationReady) _calibrationReady = Mxm.CurrentAnimData != null;
            if (_calibrationReady)
                Mxm.SetCalibrationData(sprinting ? SprintCalibration : GeneralCalibration);
        }

        /// <summary>
        /// Every MxMAnimator registers with a scene-wide MxMSearchManager on Start and throws without one.
        /// Create it here (before any MxMAnimator.Start) so the Player works in any scene, using the
        /// demo scene's settings.
        /// </summary>
        private static void EnsureSearchManager()
        {
            if (MxMSearchManager.Instance != null) return;
            var go = new GameObject("MxMSearchManager");
            go.SetActive(false); // configure before Awake runs
            var manager = go.AddComponent<MxMSearchManager>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MxMSearchManager).GetField("m_maxSearchesPerFrame", flags)?.SetValue(manager, 1);
            typeof(MxMSearchManager).GetField("m_maxAllowableDelay", flags)?.SetValue(manager, 2f);
            typeof(MxMSearchManager).GetField("m_expectedAnimatorCount", flags)?.SetValue(manager, 4);
            go.SetActive(true);
        }

        /// <summary>The hosted Animator Controller, for components that set its parameters (CharacterActions).</summary>
        public bool TryGetControllerLayer(out AnimatorControllerPlayable layer)
        {
            bool ok = ResolveControllerLayer();
            layer = _controllerLayer;
            return ok;
        }

        /// <summary>MxM keeps the controller playable private; read it once it has been created.</summary>
        private bool ResolveControllerLayer()
        {
            if (_controllerLayer.IsValid()) return true;
            if (ControllerPlayableField == null) return false;
            _controllerLayer = (AnimatorControllerPlayable)ControllerPlayableField.GetValue(Mxm);
            return _controllerLayer.IsValid();
        }
    }
}
