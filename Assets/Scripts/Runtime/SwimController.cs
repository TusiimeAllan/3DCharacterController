using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Swimming in a WaterVolume. Once the water is deeper than EnterDepth the character floats with its head
    /// just above the surface and swims camera-relative (Sprint swims faster). It stands up again where the
    /// water gets shallow, and Jump at an edge that is close above the surface climbs out.
    /// The capsule still moves through the CharacterController, so pool walls block it. The head is measured
    /// every frame and the float depth adjusted so it stays above the water whatever the swim pose.
    /// </summary>
    [DefaultExecutionOrder(-6)] // with ParkourController / ClimbController, before ThirdPersonController
    [DisallowMultipleComponent]
    public class SwimController : MonoBehaviour
    {
        public bool EnableSwimming = true;

        [Header("References")]
        public ThirdPersonController Controller;
        public ParkourController Parkour;

        [Header("Swimming")]
        public float SwimSpeed = 1.6f;
        public float SwimSprintSpeed = 2.8f;
        public float Acceleration = 3f;
        [Range(0f, 0.5f)] public float TurnSmoothTime = 0.25f;
        [Tooltip("Water deeper than this over the feet starts swimming (m)")]
        public float EnterDepth = 1.25f;
        [Tooltip("Water shallower than this under the character lets it stand up and walk (m)")]
        public float ExitDepth = 1.05f;
        [Tooltip("How far the head stays above the surface")]
        public float HeadAboveWater = 0.12f;
        [Tooltip("First guess of how deep the feet hang below the surface (corrected by measuring the head)")]
        public float FloatDepth = 1.45f;
        [Tooltip("Gentle bob on the surface (m)")]
        public float BobAmplitude = 0.03f;

        [Header("Climbing Out")]
        [Tooltip("Edges up to this high above the surface can be climbed out of with Jump")]
        public float MaxClimbOutHeight = 0.9f;

        public bool IsSwimming { get; private set; }
        public float SwimSpeedNow => _velocity.magnitude;

        private static readonly int SwimmingId = Animator.StringToHash("Swimming");
        private static readonly int SwimSpeedId = Animator.StringToHash("SwimSpeed");

        private PlayerInputs _inputs;
        private CharacterController _cc;
        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private WaterVolume _water;
        private Vector3 _velocity;
        private float _floatDepth;
        private float _turnVelocity;
        private float _cooldown;
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Parkour == null) Parkour = GetComponent<ParkourController>();
            _inputs = GetComponent<PlayerInputs>();
            _cc = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
        }

        private void OnDisable()
        {
            if (IsSwimming) Exit();
        }

        private void Update()
        {
            if (Controller == null || _inputs == null || _cc == null) return;
            float dt = Time.deltaTime;
            if (!IsSwimming)
            {
                if (_cooldown > 0f) _cooldown -= dt;
                if (!EnableSwimming || _cooldown > 0f || Controller.ExternallyDriven) return;
                var water = WaterVolume.At(transform.position);
                if (water != null && water.SurfaceY - transform.position.y > EnterDepth) Enter(water);
                return;
            }

            Controller.ExternallyDriven = true;
            Vector3 pos = transform.position;
            // Floating, the feet can be near (or above) the surface: only leaving the area ends the swim.
            if (_water == null || !_water.isActiveAndEnabled || !_water.ContainsXZ(pos))
            {
                Exit();
                return;
            }

            if (_inputs.jump)
            {
                _inputs.jump = false;
                if (TryClimbOut()) return;
            }

            // Desired swim velocity, camera-relative.
            Vector2 move = _inputs.move;
            Vector3 dir = new Vector3(move.x, 0f, move.y);
            if (Controller.CameraTransform != null)
                dir = Quaternion.Euler(0f, Controller.CameraTransform.eulerAngles.y, 0f) * dir;
            float magnitude = Mathf.Clamp01(move.magnitude);
            Vector3 target = dir.sqrMagnitude > 1e-4f
                ? dir.normalized * ((_inputs.sprint ? SwimSprintSpeed : SwimSpeed) * magnitude)
                : Vector3.zero;
            _velocity = Vector3.MoveTowards(_velocity, target, Acceleration * dt);

            // Shallow enough to stand? Walk out.
            if (GroundBelow(pos, out float groundY) && _water.SurfaceY - groundY < ExitDepth)
            {
                Exit();
                return;
            }

            if (_velocity.sqrMagnitude > 0.01f)
            {
                float yaw = Mathf.Atan2(_velocity.x, _velocity.z) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f,
                    Mathf.SmoothDampAngle(transform.eulerAngles.y, yaw, ref _turnVelocity, TurnSmoothTime), 0f);
            }

            float bob = Mathf.Sin(Time.time * 1.7f) * BobAmplitude;
            float targetY = _water.SurfaceY - _floatDepth + bob;
            float dy = (targetY - pos.y) * (1f - Mathf.Exp(-4f * dt));
            _cc.Move(_velocity * dt + Vector3.up * dy);

            SetBool(SwimmingId, true);
            SetFloat(SwimSpeedId, _velocity.magnitude);
        }

        // Keep the head above water whatever the swim pose (measured after the animation is evaluated).
        private void LateUpdate()
        {
            if (!IsSwimming || _animator == null || !_animator.isHuman || _water == null) return;
            var head = _animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;
            float error = _water.SurfaceY + HeadAboveWater - head.position.y; // > 0: head too low
            // The swim poses lie flat, so the "feet" reference can end up close to the surface.
            _floatDepth = Mathf.Clamp(_floatDepth - error * (1f - Mathf.Exp(-3f * Time.deltaTime)), -0.6f, 2.2f);
        }

        private void Enter(WaterVolume water)
        {
            _water = water;
            IsSwimming = true;
            _floatDepth = FloatDepth;
            Vector3 v = _cc.velocity;
            v.y = 0f;
            _velocity = v * 0.5f;
            Controller.ExternallyDriven = true;
            SetBool(SwimmingId, true);
        }

        private void Exit()
        {
            IsSwimming = false;
            SetBool(SwimmingId, false);
            SetFloat(SwimSpeedId, 0f);
            if (Controller != null) Controller.ExternallyDriven = false;
            _cooldown = 0.4f;
        }

        /// <summary>Jump at an edge just above the surface: climb out onto it.</summary>
        private bool TryClimbOut()
        {
            if (Parkour == null) return false;
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            fwd.Normalize();
            Vector3 origin = new Vector3(transform.position.x, _water.SurfaceY - 0.2f, transform.position.z);
            if (!Cast(origin, fwd, 0.9f, out RaycastHit wall, 0.2f)) return false;
            Vector3 face = -wall.normal;
            face.y = 0f;
            if (face.sqrMagnitude < 1e-4f || Vector3.Angle(face, fwd) > 50f) return false;
            face.Normalize();
            Vector3 over = new Vector3(wall.point.x, 0f, wall.point.z) + face * 0.2f;
            float startY = _water.SurfaceY + MaxClimbOutHeight + 0.3f;
            if (!Ray(new Vector3(over.x, startY, over.z), Vector3.down, MaxClimbOutHeight + 0.6f, out RaycastHit top) ||
                top.distance < 0.01f) return false;
            if (top.point.y < _water.SurfaceY - 0.2f) return false;

            IsSwimming = false;
            SetBool(SwimmingId, false);
            SetFloat(SwimSpeedId, 0f);
            _cooldown = 1f;
            if (Parkour.BeginFromHere(ParkourKind.ClimbFromHang, face, top.point.y)) return true;
            Exit();
            return false;
        }

        private bool GroundBelow(Vector3 pos, out float groundY)
        {
            groundY = float.MinValue;
            if (!Ray(pos + Vector3.up * 0.5f, Vector3.down, 6f, out RaycastHit hit)) return false;
            groundY = hit.point.y;
            return true;
        }

        private bool Cast(Vector3 origin, Vector3 dir, float distance, out RaycastHit best, float radius)
        {
            int n = Physics.SphereCastNonAlloc(origin, radius, dir, _hits, distance, Controller.GroundLayers,
                QueryTriggerInteraction.Ignore);
            return Nearest(n, out best);
        }

        private bool Ray(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, _hits, distance, Controller.GroundLayers,
                QueryTriggerInteraction.Ignore);
            return Nearest(n, out best);
        }

        private bool Nearest(int count, out RaycastHit best)
        {
            best = default;
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (_hits[i].transform.IsChildOf(transform)) continue;
                if (_hits[i].distance < nearest)
                {
                    nearest = _hits[i].distance;
                    best = _hits[i];
                    found = true;
                }
            }
            return found;
        }

        private void SetBool(int id, bool value)
        {
            if (_mm != null && _mm.isActiveAndEnabled && _mm.TryGetControllerLayer(out AnimatorControllerPlayable layer))
                layer.SetBool(id, value);
            else if (_animator != null && _animator.runtimeAnimatorController != null)
                _animator.SetBool(id, value);
        }

        private void SetFloat(int id, float value)
        {
            if (_mm != null && _mm.isActiveAndEnabled && _mm.TryGetControllerLayer(out AnimatorControllerPlayable layer))
                layer.SetFloat(id, value);
            else if (_animator != null && _animator.runtimeAnimatorController != null)
                _animator.SetFloat(id, value);
        }
    }
}
