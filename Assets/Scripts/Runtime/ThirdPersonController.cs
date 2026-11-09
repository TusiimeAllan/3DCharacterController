using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Camera-relative third person movement built on CharacterController.
    /// Drives a Humanoid Animator via Speed / MotionSpeed / Grounded / Jump / FreeFall / Crouch.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonController : MonoBehaviour
    {
        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;
        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 6.0f;
        [Tooltip("Crouch move speed of the character in m/s")]
        public float CrouchSpeed = 1.5f;
        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)] public float RotationSmoothTime = 0.12f;
        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        [Header("Jump / Gravity")]
        [Tooltip("The height the player can jump in meters")]
        public float JumpHeight = 1.2f;
        [Tooltip("Custom gravity. Engine default is -9.81")]
        public float Gravity = -15.0f;
        [Tooltip("Time required to pass after a jump before being able to jump again")]
        public float JumpTimeout = 0.50f;
        [Tooltip("A jump pressed this long before landing still triggers on landing")]
        public float JumpBufferTime = 0.15f;
        [Tooltip("Grace period after walking off a ledge during which a jump is still allowed")]
        public float CoyoteTime = 0.15f;
        [Tooltip("Time required to pass before entering the fall state")]
        public float FallTimeout = 0.15f;

        [Header("Grounded")]
        public bool Grounded = true;
        [Tooltip("Offset for rough ground")]
        public float GroundedOffset = -0.14f;
        [Tooltip("Radius of the grounded check. Match the CharacterController radius")]
        public float GroundedRadius = 0.28f;
        [Tooltip("What layers count as ground")]
        public LayerMask GroundLayers = ~0;

        [Header("Crouch Collider")]
        public float StandingHeight = 1.8f;
        public float CrouchingHeight = 1.2f;

        [Header("References")]
        [Tooltip("Transform the movement is oriented against. Usually the main camera.")]
        public Transform CameraTransform;
        [SerializeField] private CharacterController _controller;
        [SerializeField] private PlayerInputs _input;
        [Tooltip("Optional. Leave empty to run without animation.")]
        [SerializeField] private Animator _animator;

        [Header("Animation Output")]
        [Tooltip("Write Speed / Grounded / Jump / FreeFall / Crouch to the Animator. Turned off when another " +
                 "component (e.g. motion matching) reads the state below and drives the animation instead.")]
        public bool DriveAnimator = true;

        // Movement state published for animation systems. The controller always owns movement.
        public float TargetSpeed { get; private set; }
        public float AnimationBlend => _animationBlend;
        public float InputMagnitude { get; private set; }
        public bool IsCrouching { get; private set; }
        public bool IsJumping { get; private set; }
        public bool IsFreeFalling { get; private set; }
        public float VerticalVelocity => _verticalVelocity;
        /// <summary>Fastest downward speed of the current or just-finished fall (m/s); held briefly after landing so animation can pick a landing.</summary>
        public float FallSpeed { get; private set; }
        [Tooltip("Seconds FallSpeed keeps the landing speed after touching down")]
        public float LandingHoldTime = 0.35f;

        [Header("External Control")]
        [Tooltip("Set by actions (death, sitting, interacting): ignore move, jump and crouch input")]
        public bool MovementLocked;
        [Tooltip("Multiplies the target speed, e.g. slower while pushing")]
        public float SpeedScale = 1f;
        [Tooltip("Set by moves that place the character themselves (parkour): no input, gravity or collision moves")]
        public bool ExternallyDriven;

        [Header("Strafe")]
        [Tooltip("Holding the Strafe input keeps the character facing the camera and moves it in any direction")]
        public bool AllowStrafe = true;
        [Tooltip("Sprint speed while strafing (m/s); the strafe run clips are about 4.3 m/s")]
        public float StrafeSprintSpeed = 4.2f;
        [Tooltip("Speed multiplier when moving straight backwards while strafing")]
        [Range(0.3f, 1f)] public float StrafeBackwardScale = 0.7f;
        [Tooltip("How fast the character turns to the camera while strafing")]
        [Range(0.0f, 0.3f)] public float StrafeRotationSmoothTime = 0.05f;
        [Tooltip("Standing still while strafing, step around with the turn clips instead of sliding the feet")]
        public bool StrafeTurnInPlace = true;
        [Tooltip("Camera must be this far (degrees) from the body before a standing turn starts")]
        [Range(20f, 170f)] public float StrafeTurnThreshold = 70f;
        [Tooltip("Right after a turn, a leftover bigger than this (degrees) chains a shorter follow-up turn")]
        [Range(5f, 60f)] public float StrafeTurnSettleAngle = 20f;
        [Tooltip("Degrees one turn clip rotates (the most a single turn covers)")]
        public float StrafeTurnAngle = 90f;
        [Tooltip("Seconds one turn takes (the Mixamo turn 90 clips are about 0.9 s)")]
        public float StrafeTurnDuration = 0.9f;

        /// <summary>-1 turning left, 1 turning right, 0 not turning (standing turns while strafing).</summary>
        public int StrafeTurn => _turnDir;

        /// <summary>True while strafing: facing follows the camera, not the movement.</summary>
        public bool IsStrafing { get; private set; }
        /// <summary>Smoothed planar velocity in the character's own frame (x right, y forward), m/s.</summary>
        public Vector2 LocalVelocity { get; private set; }

        // player state
        private float _speed;
        private float _animationBlend;
        private float _targetRotation;
        private float _moveRotation;
        private float _rotationVelocity;
        private int _turnDir;
        private float _turnElapsed;
        private float _turnStartYaw;
        private float _turnAngle;
        private float _turnTime;
        private bool _turnChained;
        private float _verticalVelocity;
        private readonly float _terminalVelocity = 53.0f;

        // timeout deltas
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;
        private float _jumpBufferDelta;
        private float _coyoteDelta;
        private float _landingHold;
        private bool _wasAirborne;

        // animator parameter hashes
        private int _animSpeed;
        private int _animGrounded;
        private int _animJump;
        private int _animFreeFall;
        private int _animMotionSpeed;
        private int _animCrouch;
        private int _animFallSpeed;
        private int _animStrafing;
        private int _animStrafeX;
        private int _animStrafeZ;
        private int _animStrafeTurn;

        private bool _hasAnimator;

        private const float _threshold = 0.01f;
        private readonly Collider[] _groundHits = new Collider[8];

        private void Reset() => AutoAssignReferences();

#if UNITY_EDITOR
        private void OnValidate() => AutoAssignReferences();
#endif

        /// <summary>Editor-time convenience: fills empty references from this GameObject.</summary>
        private void AutoAssignReferences()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_input == null) _input = GetComponent<PlayerInputs>();
            if (_animator == null) _animator = GetComponent<Animator>();
        }

        private void Start()
        {
            if (_controller == null || _input == null)
            {
                Debug.LogError($"[{nameof(ThirdPersonController)}] Assign CharacterController and PlayerInputs in the Inspector.", this);
                enabled = false;
                return;
            }
            _hasAnimator = _animator != null;
            AssignAnimationIDs();

            _jumpTimeoutDelta = 0f;
            _fallTimeoutDelta = FallTimeout;

            _controller.height = StandingHeight;
            _controller.center = new Vector3(0f, StandingHeight * 0.5f, 0f);
        }

        private void Update()
        {
            _hasAnimator = _animator != null && DriveAnimator;
            if (ExternallyDriven)
            {
                // Something else (parkour) places the character: report a calm, grounded state.
                Grounded = true;
                IsJumping = IsFreeFalling = IsStrafing = false;
                _turnDir = 0;
                _verticalVelocity = -2f;
                FallSpeed = 0f;
                _wasAirborne = false;
                _speed = 0f;
                LocalVelocity = Vector2.zero;
                _input.jump = false;
                return;
            }
            GroundedCheck();
            JumpAndGravity();
            Crouch();
            Move();
        }

        private void AssignAnimationIDs()
        {
            _animSpeed = Animator.StringToHash("Speed");
            _animGrounded = Animator.StringToHash("Grounded");
            _animJump = Animator.StringToHash("Jump");
            _animFreeFall = Animator.StringToHash("FreeFall");
            _animMotionSpeed = Animator.StringToHash("MotionSpeed");
            _animCrouch = Animator.StringToHash("Crouch");
            _animFallSpeed = Animator.StringToHash("FallSpeed");
            _animStrafing = Animator.StringToHash("Strafing");
            _animStrafeX = Animator.StringToHash("StrafeX");
            _animStrafeZ = Animator.StringToHash("StrafeZ");
            _animStrafeTurn = Animator.StringToHash("StrafeTurn");
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x,
                transform.position.y - GroundedOffset, transform.position.z);
            // Ignore the player's own colliders, otherwise the check always reports grounded.
            int hits = Physics.OverlapSphereNonAlloc(spherePosition, GroundedRadius, _groundHits, GroundLayers,
                QueryTriggerInteraction.Ignore);
            Grounded = false;
            for (int i = 0; i < hits; i++)
            {
                if (!_groundHits[i].transform.IsChildOf(transform))
                {
                    Grounded = true;
                    break;
                }
            }

            if (_hasAnimator) _animator.SetBool(_animGrounded, Grounded);
        }

        private void Crouch()
        {
            if (MovementLocked) _input.crouch = IsCrouching; // keep the current stance while locked
            bool wantsCrouch = _input.crouch && Grounded;

            // Block stand-up if there is no headroom.
            if (!wantsCrouch && _controller.height < StandingHeight - 0.01f)
            {
                Vector3 start = transform.position + Vector3.up * CrouchingHeight;
                if (Physics.SphereCast(start, GroundedRadius, Vector3.up, out _,
                        StandingHeight - CrouchingHeight, GroundLayers, QueryTriggerInteraction.Ignore))
                {
                    wantsCrouch = true;
                    _input.crouch = true;
                }
            }

            float targetHeight = wantsCrouch ? CrouchingHeight : StandingHeight;
            _controller.height = Mathf.Lerp(_controller.height, targetHeight, Time.deltaTime * 12f);
            _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);

            IsCrouching = wantsCrouch;
            if (_hasAnimator) _animator.SetBool(_animCrouch, wantsCrouch);
        }

        private void Move()
        {
            Vector2 move = MovementLocked ? Vector2.zero : _input.move;
            bool crouching = _input.crouch && Grounded;
            // No crouch strafe clips: crouching keeps the normal turn-to-move behaviour.
            IsStrafing = AllowStrafe && _input.strafe && !MovementLocked && !crouching && CameraTransform != null;

            float sprintSpeed = IsStrafing ? StrafeSprintSpeed : SprintSpeed;
            float targetSpeed = (crouching ? CrouchSpeed : (_input.sprint ? sprintSpeed : MoveSpeed)) * SpeedScale;
            Vector3 inputDirection = new Vector3(move.x, 0.0f, move.y).normalized;
            // Facing the camera, "back" on the stick walks backwards: slow that down.
            if (IsStrafing) targetSpeed *= Mathf.Lerp(1f, StrafeBackwardScale, Mathf.Max(0f, -inputDirection.z));

            if (move == Vector2.zero) targetSpeed = 0.0f;
            TargetSpeed = targetSpeed;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f,
                _controller.velocity.z).magnitude;
            float speedOffset = 0.1f;
            float inputMagnitude = move.magnitude > 1f ? 1f : move.magnitude;
            InputMagnitude = inputMagnitude;

            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            if (move != Vector2.zero && CameraTransform != null)
                _moveRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                                CameraTransform.eulerAngles.y;

            bool standing = move == Vector2.zero && Grounded && !IsJumping && _speed < 0.1f;
            if (!IsStrafing || !StrafeTurnInPlace || !standing) _turnDir = 0;
            if (!IsStrafing || !standing) _turnChained = false;

            if (IsStrafing && StrafeTurnInPlace && standing)
            {
                // Standing still: let the head follow small camera moves, and step round in 90 degree
                // turns (synced to the turn clips) once the camera is far off, instead of sliding the feet.
                _targetRotation = CameraTransform.eulerAngles.y;
                _rotationVelocity = 0f;
                if (_turnDir == 0)
                {
                    float delta = Mathf.DeltaAngle(transform.eulerAngles.y, _targetRotation);
                    if (Mathf.Abs(delta) > (_turnChained ? StrafeTurnSettleAngle : StrafeTurnThreshold))
                    {
                        _turnDir = delta > 0f ? 1 : -1;
                        _turnElapsed = 0f;
                        _turnStartYaw = transform.eulerAngles.y;
                        // A shorter turn plays the start of the clip, for proportionally less time.
                        _turnAngle = Mathf.Min(Mathf.Abs(delta), StrafeTurnAngle);
                        _turnTime = Mathf.Max(0.35f, StrafeTurnDuration * _turnAngle / StrafeTurnAngle);
                    }
                    else _turnChained = false;
                }
                if (_turnDir != 0)
                {
                    _turnElapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(_turnElapsed / _turnTime);
                    float yaw = _turnStartYaw + _turnDir * _turnAngle * Mathf.SmoothStep(0f, 1f, t);
                    transform.rotation = Quaternion.Euler(0.0f, yaw, 0.0f);
                    if (t >= 1f)
                    {
                        _turnDir = 0;
                        _turnChained = true; // settle any leftover next frame
                    }
                }
            }
            else if (IsStrafing)
            {
                // Face where the camera looks; move along the stick.
                _targetRotation = CameraTransform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation,
                    ref _rotationVelocity, StrafeRotationSmoothTime);
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }
            else if (move != Vector2.zero && CameraTransform != null)
            {
                _targetRotation = _moveRotation;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation,
                    ref _rotationVelocity, RotationSmoothTime);
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _moveRotation, 0.0f) * Vector3.forward;
            Vector3 planarVelocity = targetDirection.normalized * _speed;

            _controller.Move(planarVelocity * Time.deltaTime +
                new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            // Velocity in the character's frame, for the strafe blend (smoothed so direction changes blend).
            Vector3 local = transform.InverseTransformDirection(planarVelocity);
            LocalVelocity = Vector2.Lerp(LocalVelocity, new Vector2(local.x, local.z),
                1f - Mathf.Exp(-SpeedChangeRate * Time.deltaTime));
            if (LocalVelocity.sqrMagnitude < 0.0001f) LocalVelocity = Vector2.zero;

            if (_hasAnimator)
            {
                _animator.SetFloat(_animSpeed, _animationBlend);
                _animator.SetFloat(_animMotionSpeed, inputMagnitude);
                _animator.SetBool(_animStrafing, IsStrafing);
                _animator.SetFloat(_animStrafeX, LocalVelocity.x);
                _animator.SetFloat(_animStrafeZ, LocalVelocity.y);
                _animator.SetInteger(_animStrafeTurn, _turnDir);
            }
        }

        private void JumpAndGravity()
        {
            float dt = Time.deltaTime;

            // Consume the press into a short buffer so it is neither lost nor fired late.
            if (MovementLocked) _input.jump = false;
            if (_input.jump)
            {
                _jumpBufferDelta = JumpBufferTime;
                _input.jump = false;
            }
            else if (_jumpBufferDelta > 0f)
            {
                _jumpBufferDelta -= dt;
            }

            if (_jumpTimeoutDelta > 0f) _jumpTimeoutDelta -= dt;

            if (Grounded)
            {
                _coyoteDelta = CoyoteTime;
                _fallTimeoutDelta = FallTimeout;

                IsJumping = false;
                IsFreeFalling = false;
                if (_hasAnimator)
                {
                    _animator.SetBool(_animJump, false);
                    _animator.SetBool(_animFreeFall, false);
                }

                // Keep the landing speed for a moment so animation can choose a soft land or a roll.
                if (_wasAirborne)
                {
                    _wasAirborne = false;
                    _landingHold = LandingHoldTime;
                }
                else if (_landingHold > 0f)
                {
                    _landingHold -= dt;
                    if (_landingHold <= 0f) FallSpeed = 0f;
                }

                if (_verticalVelocity < 0.0f) _verticalVelocity = -2f;
            }
            else
            {
                if (!_wasAirborne)
                {
                    _wasAirborne = true;
                    _landingHold = 0f;
                    FallSpeed = 0f;
                }
                FallSpeed = Mathf.Max(FallSpeed, -_verticalVelocity);

                if (_coyoteDelta > 0f) _coyoteDelta -= dt;

                if (_fallTimeoutDelta >= 0.0f) _fallTimeoutDelta -= dt;
                else
                {
                    IsFreeFalling = true;
                    if (_hasAnimator) _animator.SetBool(_animFreeFall, true);
                }
            }

            bool canJump = _coyoteDelta > 0f && _jumpBufferDelta > 0f && _jumpTimeoutDelta <= 0f &&
                           !_input.crouch; // no jumping while crouched
            if (canJump)
            {
                _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
                _jumpBufferDelta = 0f;
                _coyoteDelta = 0f;
                _jumpTimeoutDelta = JumpTimeout;
                IsJumping = true;
                if (_hasAnimator) _animator.SetBool(_animJump, true);
            }

            if (_hasAnimator) _animator.SetFloat(_animFallSpeed, FallSpeed);

            // Falling velocity is negative, so clamp against the negative terminal velocity.
            _verticalVelocity = Mathf.Max(_verticalVelocity + Gravity * dt, -_terminalVelocity);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);
            Gizmos.color = Grounded ? transparentGreen : transparentRed;
            Gizmos.DrawSphere(new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z), GroundedRadius);
        }
    }
}
