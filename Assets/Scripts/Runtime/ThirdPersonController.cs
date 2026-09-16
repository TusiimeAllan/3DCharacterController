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
        [Tooltip("Time required to pass before being able to jump again")]
        public float JumpTimeout = 0.50f;
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

        [Header("Camera")]
        [Tooltip("Transform the movement is oriented against. Usually the main camera.")]
        public Transform CameraTransform;

        // player state
        private float _speed;
        private float _animationBlend;
        private float _targetRotation;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private readonly float _terminalVelocity = 53.0f;

        // timeout deltas
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animator parameter hashes
        private int _animSpeed;
        private int _animGrounded;
        private int _animJump;
        private int _animFreeFall;
        private int _animMotionSpeed;
        private int _animCrouch;

        private CharacterController _controller;
        private PlayerInputs _input;
        private Animator _animator;
        private bool _hasAnimator;

        private const float _threshold = 0.01f;

        private void Awake()
        {
            if (CameraTransform == null && Camera.main != null)
                CameraTransform = Camera.main.transform;
        }

        private void Start()
        {
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<PlayerInputs>();
            _hasAnimator = TryGetComponent(out _animator);
            AssignAnimationIDs();

            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;

            _controller.height = StandingHeight;
            _controller.center = new Vector3(0f, StandingHeight * 0.5f, 0f);
        }

        private void Update()
        {
            _hasAnimator = _animator != null;
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
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x,
                transform.position.y - GroundedOffset, transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            if (_hasAnimator) _animator.SetBool(_animGrounded, Grounded);
        }

        private void Crouch()
        {
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

            if (_hasAnimator) _animator.SetBool(_animCrouch, wantsCrouch);
        }

        private void Move()
        {
            bool crouching = _input.crouch && Grounded;
            float targetSpeed = crouching ? CrouchSpeed : (_input.sprint ? SprintSpeed : MoveSpeed);

            if (_input.move == Vector2.zero) targetSpeed = 0.0f;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f,
                _controller.velocity.z).magnitude;
            float speedOffset = 0.1f;
            float inputMagnitude = _input.move.magnitude > 1f ? 1f : _input.move.magnitude;

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

            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

            if (_input.move != Vector2.zero && CameraTransform != null)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                    CameraTransform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation,
                    ref _rotationVelocity, RotationSmoothTime);
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            _controller.Move(targetDirection.normalized * (_speed * Time.deltaTime) +
                new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            if (_hasAnimator)
            {
                _animator.SetFloat(_animSpeed, _animationBlend);
                _animator.SetFloat(_animMotionSpeed, inputMagnitude);
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;

                if (_hasAnimator)
                {
                    _animator.SetBool(_animJump, false);
                    _animator.SetBool(_animFreeFall, false);
                }

                if (_verticalVelocity < 0.0f) _verticalVelocity = -2f;

                bool canJump = !(_input.crouch); // no jumping while crouched
                if (_input.jump && _jumpTimeoutDelta <= 0.0f && canJump)
                {
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
                    if (_hasAnimator) _animator.SetBool(_animJump, true);
                }

                if (_jumpTimeoutDelta >= 0.0f) _jumpTimeoutDelta -= Time.deltaTime;
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;

                if (_fallTimeoutDelta >= 0.0f) _fallTimeoutDelta -= Time.deltaTime;
                else if (_hasAnimator) _animator.SetBool(_animFreeFall, true);

                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
                _verticalVelocity += Gravity * Time.deltaTime;
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
