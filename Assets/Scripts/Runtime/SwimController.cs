using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Water: wading, swimming and diving in a WaterVolume.
    /// - Wading: water over the feet slows walking, more the deeper it gets, with splashing steps.
    /// - Swimming: once the water is deeper than EnterDepth the character floats with its head just above the
    ///   surface and swims camera-relative (Sprint swims faster). Shallow water stands it up again; Jump at an
    ///   edge just above the surface climbs out.
    /// - Diving: hold Crouch to dive and swim down, hold Jump to rise; the body pitches with the swim
    ///   direction and the camera fogs blue below the surface. Reaching the surface resumes swimming.
    /// The capsule still moves through the CharacterController, so walls and the pool floor block it. The head
    /// is measured every frame and the float depth adjusted so it stays above the water whatever the pose.
    /// </summary>
    [DefaultExecutionOrder(-6)] // with ParkourController / ClimbController, before ThirdPersonController
    [DisallowMultipleComponent]
    public class SwimController : MonoBehaviour
    {
        public bool EnableSwimming = true;

        [Header("References")]
        public ThirdPersonController Controller;
        public ParkourController Parkour;
        public WaterEffects Effects;

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
        [Tooltip("Seconds between stroke splashes at swim speed")]
        public float StrokeInterval = 0.6f;

        [Header("Diving")]
        public bool EnableDiving = true;
        [Tooltip("Vertical swim speed while diving or rising (m/s)")]
        public float DiveSpeed = 1.3f;
        [Tooltip("Slow rise when neither diving nor rising (m/s)")]
        public float Buoyancy = 0.25f;
        [Tooltip("Largest body pitch while swimming up or down (degrees)")]
        public float MaxPitch = 55f;
        [Tooltip("Closest the capsule comes to the water's edge while swimming (the body reaches ahead of it)")]
        public float EdgeMargin = 0.75f;
        [Tooltip("Closest the capsule's base comes to the bottom while diving (m)")]
        public float MinFloorClearance = 0.75f;

        [Header("Breath")]
        [Tooltip("Seconds of breath under water")]
        public float BreathSeconds = 20f;
        [Tooltip("Seconds to get a full breath back at the surface")]
        public float RefillSeconds = 4f;
        [Tooltip("Damage taken each DrownInterval once breath runs out (the character also rises)")]
        public float DrownDamage = 10f;
        public float DrownInterval = 1.5f;
        [Tooltip("Seconds between exhaled bubbles under water")]
        public Vector2 ExhaleInterval = new Vector2(2.2f, 3.8f);

        [Header("Wading")]
        [Tooltip("Water shallower than this does not slow walking (m)")]
        public float WadeStartDepth = 0.3f;
        [Tooltip("Walking speed multiplier just before the water is deep enough to swim")]
        [Range(0.2f, 1f)] public float WadeMinSpeed = 0.55f;
        public float WadeStepInterval = 0.42f;

        [Header("Climbing Out")]
        [Tooltip("Edges up to this high above the surface can be climbed out of with Jump")]
        public float MaxClimbOutHeight = 0.9f;

        public bool IsSwimming { get; private set; }
        public bool IsUnderwater { get; private set; }
        public bool IsWading { get; private set; }
        public float SwimSpeedNow => _velocity.magnitude;
        /// <summary>Breath left, 0..1.</summary>
        public float Breath { get; private set; } = 1f;

        private static readonly int SwimmingId = Animator.StringToHash("Swimming");
        private static readonly int SwimSpeedId = Animator.StringToHash("SwimSpeed");

        private PlayerInputs _inputs;
        private CharacterController _cc;
        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private WaterVolume _water;
        private Vector3 _velocity;
        private float _verticalSpeed;
        private float _floatDepth;
        private float _turnVelocity;
        private float _cooldown;
        private float _pitch;
        private float _effectTimer;
        private float _stepOffset = 0.3f;
        private float _drownTimer = 1.5f;
        private bool _gasping;
        private bool _diveLocked;
        private ThirdPersonCamera _camera;
        private float _exhaleTimer = 2f;
        private CharacterWetness _wet;
        private CharacterActions _actions;
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Parkour == null) Parkour = GetComponent<ParkourController>();
            if (Effects == null) Effects = GetComponent<WaterEffects>();
            if (Effects == null) Effects = gameObject.AddComponent<WaterEffects>();
            _inputs = GetComponent<PlayerInputs>();
            _cc = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
            _actions = GetComponent<CharacterActions>();
            _wet = GetComponent<CharacterWetness>();
            if (_wet == null) _wet = gameObject.AddComponent<CharacterWetness>();
            if (_wet.Effects == null) _wet.Effects = Effects;
        }

        private void Start()
        {
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<UnderwaterCamera>() == null) cam.gameObject.AddComponent<UnderwaterCamera>();
            var meter = new GameObject("BreathMeter").AddComponent<BreathMeter>();
            meter.Swim = this;
        }

        private void OnDisable()
        {
            if (IsSwimming) Exit();
            if (Controller != null) Controller.WadeSpeedFactor = 1f;
        }

        private void Update()
        {
            if (Controller == null || _inputs == null || _cc == null) return;
            float dt = Time.deltaTime;
            UpdateBreath(dt);
            if (_wet != null)
            {
                _wet.InWater = IsSwimming || IsWading;
                if (IsSwimming) _wet.SoakTo(1f);
            }
            if (!IsSwimming)
            {
                if (_cooldown > 0f) _cooldown -= dt;
                var water = WaterVolume.At(transform.position);
                UpdateWading(water, dt);
                if (!EnableSwimming || _cooldown > 0f || Controller.ExternallyDriven) return;
                if (water != null && water.SurfaceY - transform.position.y > EnterDepth) Enter(water);
                return;
            }

            Controller.ExternallyDriven = true;
            Controller.WadeSpeedFactor = 1f;
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
                if (!IsUnderwater && TryClimbOut()) return;
            }

            // Horizontal: camera-relative.
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

            float floatY = _water.SurfaceY - _floatDepth;
            float dy;
            if (!_inputs.crouchHeld) _diveLocked = false;
            if (!IsUnderwater && EnableDiving && _inputs.crouchHeld && !_diveLocked)
            {
                IsUnderwater = true;
                _verticalSpeed = 0f;
                Effects?.Stroke(new Vector3(pos.x, _water.SurfaceY, pos.z), 0.6f);
                Effects?.Bubbles(pos + Vector3.up * 0.4f, 14, _water.SurfaceY);
            }

            if (IsUnderwater)
            {
                float wanted = (_inputs.jumpHeld ? DiveSpeed : 0f) - (_inputs.crouchHeld ? DiveSpeed : 0f);
                if (wanted == 0f) wanted = Buoyancy;
                if (Breath <= 0f) _gasping = true;
                if (_gasping) wanted = DiveSpeed; // out of breath: straight up until the head is out
                UnderwaterBubbles(pos, dt);
                _verticalSpeed = Mathf.MoveTowards(_verticalSpeed, wanted, 4f * dt);
                dy = _verticalSpeed * dt;
                // Keep clear of the bottom (the pitched body reaches below the capsule's base); level out there.
                if (GroundBelow(pos, out float floorY) && pos.y + dy < floorY + MinFloorClearance)
                {
                    dy = Mathf.Max(0f, floorY + MinFloorClearance - pos.y) * (1f - Mathf.Exp(-6f * dt));
                    if (_verticalSpeed < 0f) _verticalSpeed = 0f;
                }
                var headBone = _animator != null ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
                bool headOut = headBone != null && headBone.position.y > _water.SurfaceY + 0.05f;
                if ((pos.y + dy >= floatY || headOut) && _verticalSpeed > 0f)
                {
                    if (_gasping) _diveLocked = true; // catch a breath: let go of Crouch before diving again
                    _gasping = false;
                    // Back at the surface.
                    IsUnderwater = false;
                    dy = floatY - pos.y;
                    Effects?.Splash(new Vector3(pos.x, _water.SurfaceY, pos.z), 0.25f);
                }
            }
            else
            {
                _verticalSpeed = 0f;
                float bob = Mathf.Sin(Time.time * 1.7f) * BobAmplitude;
                dy = (floatY + bob - pos.y) * (1f - Mathf.Exp(-4f * dt));
                StrokeEffects(pos, dt);
            }
            _cc.Move(_velocity * dt + Vector3.up * dy);
            KeepInsideWater();

            SetBool(SwimmingId, true);
            SetFloat(SwimSpeedId, Mathf.Max(_velocity.magnitude, IsUnderwater ? Mathf.Abs(_verticalSpeed) : 0f));
        }

        /// <summary>
        /// Floating, the capsule's base is near the surface, level with pool edges: without this the step offset
        /// would carry the swimmer over a wall. Getting out is by the shallows or by climbing (Jump).
        /// </summary>
        private void KeepInsideWater()
        {
            var b = _water.Bounds;
            // The swim pose lies flat: head and hands reach well ahead of the capsule, so keep that far off the edge.
            float r = Mathf.Max(_cc.radius, EdgeMargin);
            Vector3 p = transform.position;
            Vector3 clamped = new Vector3(Mathf.Clamp(p.x, b.min.x + r, b.max.x - r), p.y,
                Mathf.Clamp(p.z, b.min.z + r, b.max.z - r));
            if ((clamped - p).sqrMagnitude > 1e-8f)
            {
                transform.position = clamped;
                Physics.SyncTransforms();
            }
        }

        private void UpdateWading(WaterVolume water, float dt)
        {
            float depth = water != null ? water.SurfaceY - transform.position.y : 0f;
            IsWading = water != null && depth > WadeStartDepth && Controller.Grounded;
            if (IsWading && _wet != null) _wet.SoakTo(Mathf.Clamp01(depth / 1.6f) * 0.9f);
            Controller.WadeSpeedFactor = IsWading
                ? Mathf.Lerp(1f, WadeMinSpeed, Mathf.InverseLerp(WadeStartDepth, EnterDepth, depth))
                : 1f;
            if (!IsWading) return;
            Vector3 v = _cc.velocity;
            v.y = 0f;
            if (v.magnitude < 0.6f) return;
            _effectTimer -= dt;
            if (_effectTimer > 0f) return;
            _effectTimer = WadeStepInterval * Mathf.Clamp(1.5f / v.magnitude, 0.6f, 1.4f);
            Vector3 p = transform.position + v.normalized * 0.3f;
            Effects?.Stroke(new Vector3(p.x, water.SurfaceY, p.z), 0.2f + 0.3f * Mathf.InverseLerp(WadeStartDepth, EnterDepth, depth));
        }

        private void UpdateBreath(float dt)
        {
            bool headUnder = false;
            if (IsSwimming && IsUnderwater && _water != null)
            {
                var head = _animator != null ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
                headUnder = head == null || head.position.y < _water.SurfaceY;
            }
            if (headUnder)
            {
                Breath = Mathf.Max(0f, Breath - dt / Mathf.Max(1f, BreathSeconds));
                if (Breath <= 0f)
                {
                    _drownTimer -= dt;
                    if (_drownTimer <= 0f)
                    {
                        _drownTimer = DrownInterval;
                        if (_actions != null && _actions.isActiveAndEnabled && !_actions.IsDead)
                            _actions.TakeHit(DrownDamage, false);
                    }
                }
            }
            else
            {
                Breath = Mathf.Min(1f, Breath + dt / Mathf.Max(0.5f, RefillSeconds));
                // A bob above the surface while out of breath is not a breath: keep the timer running.
                if (Breath > 0.05f) _drownTimer = DrownInterval;
            }

            // Dive the camera with the swimmer: orbit the head rather than the standing-height socket.
            if (_camera == null) _camera = FindAnyObjectByType<ThirdPersonCamera>();
            if (_camera != null && _camera.Target != null)
            {
                float wanted = 0f;
                var head = _animator != null ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
                if (IsSwimming && IsUnderwater && head != null) wanted = head.position.y + 0.15f - _camera.Target.position.y;
                _camera.PivotOffset = Mathf.Lerp(_camera.PivotOffset, Mathf.Min(0f, wanted), 1f - Mathf.Exp(-4f * dt));
            }
        }

        /// <summary>Exhaled bubbles from the head now and then, and a few from the hands on each stroke.</summary>
        private void UnderwaterBubbles(Vector3 pos, float dt)
        {
            if (Effects == null || _animator == null) return;
            _exhaleTimer -= dt;
            if (_exhaleTimer <= 0f)
            {
                _exhaleTimer = Random.Range(ExhaleInterval.x, ExhaleInterval.y) * (Breath < 0.25f ? 0.4f : 1f);
                var head = _animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) Effects.Bubbles(head.position + transform.forward * 0.12f, Random.Range(5, 10), _water.SurfaceY);
            }
            float speed = _velocity.magnitude + Mathf.Abs(_verticalSpeed);
            if (speed < 0.4f) return;
            _effectTimer -= dt;
            if (_effectTimer > 0f) return;
            _effectTimer = StrokeInterval * Mathf.Clamp(SwimSpeed / speed, 0.6f, 1.4f);
            foreach (var b in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
            {
                var hand = _animator.GetBoneTransform(b);
                if (hand != null) Effects.Bubbles(hand.position, Random.Range(2, 4), _water.SurfaceY);
            }
        }

        private void StrokeEffects(Vector3 pos, float dt)
        {
            float speed = _velocity.magnitude;
            if (speed < 0.5f) return;
            _effectTimer -= dt;
            if (_effectTimer > 0f) return;
            _effectTimer = StrokeInterval * Mathf.Clamp(SwimSpeed / speed, 0.6f, 1.4f);
            Vector3 p = pos + transform.forward * 0.6f;
            Effects?.Stroke(new Vector3(p.x, _water.SurfaceY, p.z), Mathf.InverseLerp(0.5f, SwimSprintSpeed, speed));
        }

        // Keep the head above water whatever the swim pose (measured after the animation is evaluated).
        private void LateUpdate()
        {
            if (!IsSwimming || IsUnderwater || _animator == null || !_animator.isHuman || _water == null) return;
            var head = _animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;
            float error = _water.SurfaceY + HeadAboveWater - head.position.y; // > 0: head too low
            // The swim poses lie flat, so the "feet" reference can end up close to the surface.
            _floatDepth = Mathf.Clamp(_floatDepth - error * (1f - Mathf.Exp(-3f * Time.deltaTime)), -0.6f, 2.2f);
        }

        // Pitch the body with the swim direction underwater.
        private void OnAnimatorIK(int layerIndex)
        {
            if (_animator == null) return;
            float target = 0f;
            if (IsSwimming && IsUnderwater)
            {
                float horizontal = Mathf.Max(_velocity.magnitude, 0.6f);
                target = Mathf.Clamp(Mathf.Atan2(-_verticalSpeed, horizontal) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
            }
            _pitch = Mathf.Lerp(_pitch, target, 1f - Mathf.Exp(-5f * Time.deltaTime));
            if (Mathf.Abs(_pitch) > 0.05f)
                _animator.bodyRotation = Quaternion.AngleAxis(_pitch, transform.right) * _animator.bodyRotation;
        }

        /// <summary>Stops swimming at once (e.g. before a teleport).</summary>
        public void Cancel()
        {
            if (IsSwimming) Exit();
        }

        private void Enter(WaterVolume water)
        {
            _water = water;
            IsSwimming = true;
            IsUnderwater = false;
            _floatDepth = FloatDepth;
            Vector3 v = _cc.velocity;
            float fall = -Mathf.Min(v.y, Controller.VerticalVelocity);
            v.y = 0f;
            _velocity = v * 0.5f;
            Controller.ExternallyDriven = true;
            Controller.WadeSpeedFactor = 1f;
            _stepOffset = _cc.stepOffset;
            _cc.stepOffset = 0f; // never step up out of the water
            SetBool(SwimmingId, true);
            Effects?.Splash(new Vector3(transform.position.x, water.SurfaceY, transform.position.z),
                Mathf.Max(0.35f, Mathf.InverseLerp(1f, 9f, fall)));
        }

        private void Exit()
        {
            RestoreStep();
            IsSwimming = false;
            IsUnderwater = false;
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

            RestoreStep();
            IsSwimming = false;
            SetBool(SwimmingId, false);
            SetFloat(SwimSpeedId, 0f);
            _cooldown = 1f;
            if (Parkour.BeginFromHere(ParkourKind.ClimbFromHang, face, top.point.y,
                    new Vector3(wall.point.x, top.point.y, wall.point.z))) return true;
            Exit();
            return false;
        }

        private void RestoreStep()
        {
            if (IsSwimming && _cc != null) _cc.stepOffset = _stepOffset;
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
