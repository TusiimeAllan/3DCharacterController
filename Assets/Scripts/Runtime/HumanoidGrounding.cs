using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Procedural grounding and look-at on Unity's Humanoid IK, applied on top of whatever plays
    /// (motion matching or the Animator Controller):
    /// - each foot is raycast against the real surface and planted on it, keeping the lift the animation
    ///   gives it during a step and tilting it to the surface normal;
    /// - the pelvis drops so the lower foot can reach on stairs, slopes and rocks (the capsule rests on
    ///   edges, so without this the lower leg dangles in the air);
    /// - the head and upper body turn towards where the camera is looking.
    /// Works through the Avatar, not bone references, so it keeps working when CharacterSwitcher swaps models.
    /// Needs the IK pass enabled (MxM: Apply Playable IK, Animator Controller: layer IK Pass).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public class HumanoidGrounding : MonoBehaviour
    {
        public bool EnableGrounding = true;
        public bool EnableLookAt = true;
#if ENABLE_INPUT_SYSTEM
        [Tooltip("Toggle both features in play mode, to compare")]
        public Key ToggleKey = Key.G;
#endif

        [Header("References")]
        public ThirdPersonController Controller;
        public Transform CameraTransform;

        [Header("Feet")]
        public LayerMask GroundLayers = ~0;
        [Tooltip("How far above the animated foot the ground ray starts (lets the foot step UP onto a ledge)")]
        public float RayStartHeight = 0.5f;
        [Tooltip("How far below the character base the ray may find ground (how far a foot can reach DOWN)")]
        public float MaxStepDown = 0.55f;
        [Tooltip("Steeper surfaces than this are ignored for foot placement (walls, box sides)")]
        [Range(0f, 80f)] public float MaxFootAngle = 50f;
        [Tooltip("Speed at which feet follow new targets")]
        public float FootSmoothing = 18f;

        [Header("Pelvis")]
        [Tooltip("Furthest the pelvis may drop to let the lower foot reach")]
        public float MaxPelvisDrop = 0.45f;
        public float PelvisSmoothing = 10f;

        [Header("Look At")]
        [Range(0f, 1f)] public float LookWeight = 0.8f;
        [Range(0f, 1f)] public float BodyWeight = 0.15f;
        [Range(0f, 1f)] public float HeadWeight = 0.7f;
        [Range(0f, 1f)] public float EyesWeight = 1f;
        [Tooltip("0 = unlimited, 1 = locked straight ahead")]
        [Range(0f, 1f)] public float LookClamp = 0.55f;
        public float LookSmoothing = 6f;

        public event System.Action<bool> ToggledChanged;

        private Animator _animator;
        private MotionMatchingLocomotion _locomotion;
        private CharacterActions _actions;
        private float _ikWeight;
        private float _pelvisOffset;
        private float _lookWeight;
        private bool _posed;
        private Vector3 _lookTarget;
        private int _solvedFrame = -1;
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private struct Foot
        {
            public AvatarIKGoal Goal;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Initialised;
        }

        private Foot _left = new Foot { Goal = AvatarIKGoal.LeftFoot };
        private Foot _right = new Foot { Goal = AvatarIKGoal.RightFoot };

        public bool Enabled => EnableGrounding || EnableLookAt;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _locomotion = GetComponent<MotionMatchingLocomotion>();
            _actions = GetComponent<CharacterActions>();
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (CameraTransform == null && Controller != null) CameraTransform = Controller.CameraTransform;
            if (Controller != null && GroundLayers == ~0) GroundLayers = Controller.GroundLayers;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current[ToggleKey].wasPressedThisFrame)
            {
                bool on = !Enabled;
                EnableGrounding = EnableLookAt = on;
                ToggledChanged?.Invoke(on);
            }
#endif
        }

        private void OnAnimatorIK(int layerIndex)
        {
            // Playable graphs and multi-layer controllers can call this more than once per frame.
            if (_solvedFrame == Time.frameCount) return;
            _solvedFrame = Time.frameCount;
            if (_animator == null || !_animator.isHuman) return;

            float dt = Time.deltaTime;
            // Rolls and landings move the feet on purpose; don't pin them to the ground then.
            // Deaths, seats and full-body actions place the whole body themselves.
            bool posed = (_actions != null && _actions.isActiveAndEnabled && _actions.OverridesPose) ||
                         (Controller != null && Controller.ExternallyDriven); // parkour places the body itself
            bool grounded = (Controller == null || Controller.Grounded) &&
                            (_locomotion == null || !_locomotion.InLandingState) && !posed;
            _posed = posed;
            _ikWeight = Mathf.MoveTowards(_ikWeight, EnableGrounding && grounded ? 1f : 0f, dt * 6f);

            if (_ikWeight > 0.001f) SolveFeet(dt);
            else
            {
                _pelvisOffset = Mathf.Lerp(_pelvisOffset, 0f, dt * PelvisSmoothing);
                _left.Initialised = _right.Initialised = false;
            }

            SolveLookAt(dt);
        }

        private void SolveFeet(float dt)
        {
            Vector3 up = transform.up;
            float baseY = transform.position.y;

            bool leftHit = FootTarget(ref _left, up, baseY, out Vector3 leftTarget, out Quaternion leftRot, out float leftDelta);
            bool rightHit = FootTarget(ref _right, up, baseY, out Vector3 rightTarget, out Quaternion rightRot, out float rightDelta);

            // Drop the pelvis by however far the lower foot needs to reach down (never raise it:
            // the capsule already sits on the highest support).
            float drop = Mathf.Min(leftHit ? leftDelta : 0f, rightHit ? rightDelta : 0f);
            drop = Mathf.Clamp(drop, -MaxPelvisDrop, 0f);
            _pelvisOffset = Mathf.Lerp(_pelvisOffset, drop, dt * PelvisSmoothing);
            _animator.bodyPosition += up * (_pelvisOffset * _ikWeight);

            ApplyFoot(ref _left, leftHit, leftTarget, leftRot, dt);
            ApplyFoot(ref _right, rightHit, rightTarget, rightRot, dt);
        }

        /// <summary>
        /// Where this foot should be: the ground under the animated foot, plus the height the animation
        /// currently lifts it above the character base (so swing phases are kept).
        /// delta = how much higher or lower that target is than the animated foot.
        /// </summary>
        private bool FootTarget(ref Foot foot, Vector3 up, float baseY, out Vector3 target, out Quaternion rotation,
            out float delta)
        {
            Vector3 animated = _animator.GetIKPosition(foot.Goal);
            Quaternion animatedRot = _animator.GetIKRotation(foot.Goal);
            float footHeight = foot.Goal == AvatarIKGoal.LeftFoot ? _animator.leftFeetBottomHeight : _animator.rightFeetBottomHeight;
            float lift = Mathf.Max(0f, animated.y - footHeight - baseY);

            target = animated;
            rotation = animatedRot;
            delta = 0f;

            Vector3 origin = new Vector3(animated.x, baseY + RayStartHeight, animated.z);
            float distance = RayStartHeight + MaxStepDown;
            if (!Raycast(origin, -up, distance, out RaycastHit hit)) return false;
            if (Vector3.Angle(hit.normal, up) > MaxFootAngle) return false;

            target = hit.point + up * (footHeight + lift);
            rotation = Quaternion.FromToRotation(up, hit.normal) * animatedRot;
            delta = target.y - animated.y;
            return true;
        }

        private void ApplyFoot(ref Foot foot, bool hit, Vector3 target, Quaternion rotation, float dt)
        {
            if (!hit)
            {
                foot.Initialised = false;
                return; // no surface under this foot: leave the animated pose alone
            }

            // Pelvis moved the whole body; targets are absolute, so the solver bends the knee to reach.
            if (!foot.Initialised)
            {
                foot.Position = target;
                foot.Rotation = rotation;
                foot.Initialised = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-FootSmoothing * dt);
                foot.Position = Vector3.Lerp(foot.Position, target, k);
                foot.Rotation = Quaternion.Slerp(foot.Rotation, rotation, k);
            }

            _animator.SetIKPositionWeight(foot.Goal, _ikWeight);
            _animator.SetIKRotationWeight(foot.Goal, _ikWeight);
            _animator.SetIKPosition(foot.Goal, foot.Position);
            _animator.SetIKRotation(foot.Goal, foot.Rotation);
        }

        private void SolveLookAt(float dt)
        {
            float target = 0f;
            if (EnableLookAt && CameraTransform != null && !_posed)
            {
                Vector3 camForward = CameraTransform.forward;
                // Only look where the character could turn its head: not behind it.
                float facing = Vector3.Dot(new Vector3(camForward.x, 0f, camForward.z).normalized, transform.forward);
                target = Mathf.InverseLerp(-0.1f, 0.4f, facing) * LookWeight;
                Vector3 head = _animator.GetBoneTransform(HumanBodyBones.Head) != null
                    ? _animator.GetBoneTransform(HumanBodyBones.Head).position
                    : transform.position + Vector3.up * 1.6f;
                Vector3 desired = head + camForward * 10f;
                _lookTarget = _lookWeight <= 0.001f ? desired : Vector3.Lerp(_lookTarget, desired, 1f - Mathf.Exp(-LookSmoothing * dt));
            }

            _lookWeight = Mathf.MoveTowards(_lookWeight, target, dt * 2f);
            _animator.SetLookAtWeight(_lookWeight, BodyWeight, HeadWeight, EyesWeight, LookClamp);
            if (_lookWeight > 0.001f) _animator.SetLookAtPosition(_lookTarget);
        }

        private bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit best)
        {
            best = default;
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, distance, GroundLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (_hits[i].transform.IsChildOf(transform)) continue; // skip our own colliders
                if (_hits[i].distance < nearest)
                {
                    nearest = _hits[i].distance;
                    best = _hits[i];
                    found = true;
                }
            }
            return found;
        }
    }
}
