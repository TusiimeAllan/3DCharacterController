using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Context-sensitive parkour: while running forward the character looks ahead, measures what is there
    /// and, if it fits a move, performs it on its own:
    /// - low, thin obstacle: hurdle over it;
    /// - ledge of 0.6 to 2.5 m with room on top: climb onto it;
    /// - drop of 0.6 to 3.2 m ahead: jump down (rolling from high ledges).
    /// The animation plays on the Animator Controller (states built by ParkourBuilder). The controller stays
    /// in charge: the capsule follows the clip's own root path, stretched to the measured obstacle, so hands
    /// and feet meet the real ledge whatever its exact size.
    /// </summary>
    // Before ThirdPersonController: a move that starts this frame takes over before the controller reacts to
    // the same input (a crouch press would otherwise brake the sprint a slide needs).
    [DefaultExecutionOrder(-10)]
    [DisallowMultipleComponent]
    public class ParkourController : MonoBehaviour
    {
        public ParkourLibrary Library;
        public bool EnableParkour = true;
#if ENABLE_INPUT_SYSTEM
        [Tooltip("Toggle parkour in play mode")]
        public Key ToggleKey = Key.P;
#endif

        [Header("References")]
        public ThirdPersonController Controller;

        [Header("Detection")]
        [Tooltip("Slowest horizontal speed that triggers a move (m/s)")]
        public float MinSpeed = 1.2f;
        [Tooltip("Largest angle between the wall's facing and the run direction")]
        [Range(0f, 80f)] public float MaxApproachAngle = 40f;
        public float ProbeRadius = 0.15f;
        [Tooltip("Lower than this is a step (handled by the step offset), not parkour")]
        public float MinObstacleHeight = 0.35f;
        public float MaxHurdleHeight = 0.95f;
        public float MaxHurdleDepth = 0.9f;
        public float MaxMediumClimb = 1.75f;
        public float MaxClimbHeight = 2.5f;
        public float MinDrop = 0.6f;
        public float MaxLowDrop = 1.5f;
        public float MaxDrop = 3.2f;
        [Tooltip("Seconds before another move can start")]
        public float Cooldown = 0.3f;

        [Header("Slide (sprint, then crouch)")]
        public bool EnableSlide = true;
        [Tooltip("Slowest speed a crouch press turns into a slide (m/s)")]
        public float MinSlideSpeed = 4.5f;
        [Tooltip("Capsule height while sliding, so it fits under low gaps")]
        public float SlideHeight = 1.0f;

        public bool IsActive { get; private set; }
        public ParkourKind? CurrentKind => IsActive ? _move.Kind : (ParkourKind?)null;
        public event System.Action<bool> ToggledChanged;

        private static readonly int ParkourId = Animator.StringToHash("Parkour");
        private const float ClearParamAfter = 0.15f;

        private CharacterController _cc;
        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private PlayerInputs _inputs;
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private ParkourMove _move;
        private Vector3 _origin;
        private Vector3 _forward;
        private Quaternion _startRotation;
        private float _forwardScale, _heightScale, _endY, _elapsed, _cooldown;
        private bool _paramSet;
        private bool _collide;      // move with collisions (slide) instead of placing the capsule (vaults)
        private bool _previousCrouch;
        private float _stepOffset;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            _cc = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
            _inputs = GetComponent<PlayerInputs>();
        }

        private void OnDisable()
        {
            if (IsActive) Finish();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current[ToggleKey].wasPressedThisFrame)
            {
                EnableParkour = !EnableParkour;
                ToggledChanged?.Invoke(EnableParkour);
            }
#endif
            if (IsActive)
            {
                Step(Time.deltaTime);
                return;
            }
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;
            bool crouchPressed = _inputs != null && _inputs.crouch && !_previousCrouch;
            _previousCrouch = _inputs != null && _inputs.crouch;
            if (crouchPressed && TrySlide()) return;
            if (CanStart()) TryStart();
        }

        // ---------------------------------------------------------------- detection

        private bool CanStart()
        {
            if (!EnableParkour || Library == null || Library.Moves.Count == 0 || _cooldown > 0f) return false;
            if (Controller == null || !Controller.isActiveAndEnabled || _cc == null || !_cc.enabled) return false;
            if (!Controller.Grounded || Controller.MovementLocked || Controller.ExternallyDriven ||
                Controller.IsCrouching || Controller.IsStrafing || Controller.IsJumping) return false;
            if (Controller.InputMagnitude < 0.5f) return false;
            Vector3 v = _cc.velocity;
            v.y = 0f;
            if (v.magnitude < MinSpeed) return false;
            // Only when running the way the character faces (not mid-turn).
            return Vector3.Angle(v, Flat(transform.forward)) < 30f;
        }

        private void TryStart()
        {
            Vector3 pos = transform.position;
            Vector3 fwd = Flat(transform.forward);
            Vector3 up = Vector3.up;
            Vector3 v = _cc.velocity;
            v.y = 0f;
            float lookAhead = Mathf.Max(1.6f, v.magnitude * 0.75f);

            // Something in the way at knee height?
            Vector3 probe = pos + up * 0.45f;
            if (SphereCast(probe, fwd, lookAhead, out RaycastHit wall))
            {
                if (wall.rigidbody != null && !wall.rigidbody.isKinematic) return; // pushable: let CharacterActions push it
                Vector3 face = Flat(-wall.normal);
                if (face == Vector3.zero || Vector3.Angle(face, fwd) > MaxApproachAngle) return;
                // A real wall rises straight from the floor: a probe at ankle height meets the same face at the
                // same distance. Stairs and ramp feet are met much closer, so they are walked, not vaulted.
                if (!SphereCast(pos + up * (ProbeRadius + 0.05f), fwd, lookAhead, out RaycastHit low) ||
                    Mathf.Abs(low.distance - wall.distance) > 0.25f ||
                    Vector3.Angle(low.normal, wall.normal) > 20f) return;
                TryObstacle(pos, face, wall);
                return;
            }

            TryDrop(pos, fwd);
        }

        private void TryObstacle(Vector3 pos, Vector3 face, RaycastHit wall)
        {
            // Distance to the wall face along the approach direction.
            float faceDist = Vector3.Dot(wall.point - pos, face);
            if (faceDist < 0.2f) return;

            // Top of the obstacle just past its face.
            Vector3 over = new Vector3(wall.point.x, 0f, wall.point.z) + face * 0.15f;
            float probeTop = pos.y + MaxClimbHeight + 0.4f;
            if (!Raycast(new Vector3(over.x, probeTop, over.z), Vector3.down, MaxClimbHeight + 0.4f, out RaycastHit top) ||
                top.distance < 0.01f) return; // too tall (the ray starts inside it)
            if (Vector3.Angle(top.normal, Vector3.up) > 30f) return; // not a surface you can land on
            float height = top.point.y - pos.y;
            if (height < MinObstacleHeight || height > MaxClimbHeight) return;

            // Depth: how far the top continues at about the same level.
            float depth = 0f;
            for (float d = 0.2f; d <= 2.0f; d += 0.2f)
            {
                Vector3 p = over + face * d;
                if (!Raycast(new Vector3(p.x, top.point.y + 0.3f, p.z), Vector3.down, 0.5f, out RaycastHit h) ||
                    Mathf.Abs(h.point.y - top.point.y) > 0.15f) break;
                depth = d;
            }

            ParkourKind kind;
            float endY;
            float contact = faceDist;
            if (height <= MaxHurdleHeight && depth <= MaxHurdleDepth)
            {
                // Land on the far side, at about our own level.
                Vector3 far = over + face * (depth + 0.9f);
                if (!Raycast(new Vector3(far.x, top.point.y + 0.3f, far.z), Vector3.down, height + 1.5f, out RaycastHit land) ||
                    Mathf.Abs(land.point.y - pos.y) > 0.5f || land.point.y > top.point.y - 0.3f) return;
                kind = ParkourKind.Hurdle;
                endY = land.point.y;
                contact = faceDist + 0.15f + depth * 0.5f; // hurdle centre
            }
            else if (height >= 0.6f && depth >= 0.6f)
            {
                kind = height <= MaxMediumClimb ? ParkourKind.ClimbMedium : ParkourKind.ClimbHigh;
                endY = top.point.y;
                // Room to stand on top?
                Vector3 stand = over + face * 0.5f;
                Vector3 bottom = new Vector3(stand.x, top.point.y + _cc.radius + 0.05f, stand.z);
                if (Physics.CheckCapsule(bottom, bottom + Vector3.up * (Controller.StandingHeight - 2f * _cc.radius),
                        _cc.radius * 0.9f, Controller.GroundLayers, QueryTriggerInteraction.Ignore)) return;
            }
            else return;

            float hgt = kind == ParkourKind.Hurdle ? height + 0.2f : height;
            Begin(kind, pos, face, contact, hgt, endY);
        }

        private void TryDrop(Vector3 pos, Vector3 fwd)
        {
            // Walk the ground ahead until it falls away.
            float edge = -1f;
            float groundY = 0f;
            float previousDrop = 0f;
            for (float d = 0.2f; d <= 1.6f; d += 0.1f)
            {
                Vector3 p = pos + fwd * d;
                bool hit = Raycast(p + Vector3.up * 0.5f, Vector3.down, MaxDrop + 0.6f, out RaycastHit h);
                float drop = hit ? pos.y - h.point.y : float.MaxValue;
                if (drop > MinDrop)
                {
                    if (!hit) return; // nothing below within reach: just fall
                    if (previousDrop > 0.2f) return; // a slope going down, not a ledge
                    edge = d;
                    // Sample the landing a little further out so the roll has room.
                    Vector3 l = pos + fwd * (d + 0.8f);
                    if (!Raycast(l + Vector3.up * 0.5f, Vector3.down, MaxDrop + 0.6f, out RaycastHit land)) return;
                    groundY = land.point.y;
                    break;
                }
                previousDrop = drop;
            }
            if (edge < 0f) return;
            float total = pos.y - groundY;
            if (total < MinDrop || total > MaxDrop) return;
            var kind = total <= MaxLowDrop ? ParkourKind.DropLow : ParkourKind.DropHigh;
            Begin(kind, pos, fwd, edge, total, groundY);
        }

        private bool TrySlide()
        {
            if (!EnableParkour || !EnableSlide || Library == null || _cooldown > 0f || !_inputs.sprint) return false;
            if (Controller == null || !Controller.Grounded || Controller.MovementLocked || Controller.ExternallyDriven ||
                Controller.IsStrafing || Controller.IsJumping || _cc == null || !_cc.enabled) return false;
            Vector3 v = _cc.velocity;
            v.y = 0f;
            if (v.magnitude < MinSlideSpeed) return false;

            var options = new List<int>();
            for (int i = 0; i < Library.Moves.Count; i++)
                if (Library.Moves[i].Kind == ParkourKind.Slide && Library.Moves[i].Path != null && Library.Moves[i].Path.Length > 1) options.Add(i);
            if (options.Count == 0) return false;
            int index = options[Random.Range(0, options.Count)];
            var move = Library.Moves[index];

            // Keep the speed we arrive with: stretch the recorded slide to it.
            float clipSpeed = move.Sample(1f).x / Mathf.Max(0.1f, move.Duration);
            _forwardScale = Mathf.Clamp(v.magnitude / Mathf.Max(0.5f, clipSpeed), 0.7f, 1.4f);
            _heightScale = 1f;
            _move = move;
            _origin = transform.position;
            _forward = Flat(v);
            _startRotation = transform.rotation;
            _endY = _origin.y;
            _elapsed = 0f;
            _collide = true;
            IsActive = true;
            _paramSet = true;
            SetParkourParam(index + 1);

            _inputs.crouch = false; // the slide is the crouch; stand up after unless there is no headroom
            _previousCrouch = false;
            Controller.ExternallyDriven = true;
            _cc.height = SlideHeight;
            _cc.center = new Vector3(0f, SlideHeight * 0.5f, 0f);
            // The controller lifts by its step offset while testing a move; keep that from catching low roofs.
            _stepOffset = _cc.stepOffset;
            _cc.stepOffset = Mathf.Min(_stepOffset, 0.1f);
            return true;
        }

        // ---------------------------------------------------------------- execution

        private void Begin(ParkourKind kind, Vector3 pos, Vector3 face, float contactDistance, float height, float endY)
        {
            var options = new List<int>();
            for (int i = 0; i < Library.Moves.Count; i++)
                if (Library.Moves[i].Kind == kind && Library.Moves[i].Path != null && Library.Moves[i].Path.Length > 1) options.Add(i);
            if (options.Count == 0) return;
            int index = options[Random.Range(0, options.Count)];
            var move = Library.Moves[index];

            // Start once the obstacle is about where it is in the recording; stretch the rest.
            float scale = contactDistance / Mathf.Max(0.1f, move.ContactDistance);
            if (scale > 1.3f) return; // still too far: keep running, try again next frame
            _forwardScale = Mathf.Max(0.35f, scale);
            _heightScale = height / Mathf.Max(0.05f, move.Height);

            _move = move;
            _origin = pos;
            _forward = face;
            _startRotation = transform.rotation;
            _endY = endY;
            _elapsed = 0f;
            _collide = false;
            IsActive = true;
            _paramSet = true;
            SetParkourParam(index + 1);

            Controller.ExternallyDriven = true;
            _cc.enabled = false; // the path places the capsule; re-enabled when the move ends
        }

        private void Step(float dt)
        {
            _elapsed += dt;
            if (_paramSet && _elapsed >= ClearParamAfter)
            {
                _paramSet = false;
                SetParkourParam(0);
            }

            float p = Mathf.Clamp01(_elapsed / Mathf.Max(0.05f, _move.Duration));
            Vector2 s = _move.Sample(p);
            Vector2 end = _move.Sample(1f);
            float y = _origin.y + s.y * _heightScale;
            // Land exactly on the measured surface.
            float endError = _endY - (_origin.y + end.y * _heightScale);
            y += endError * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, p));
            // Crouches and rolls already lower the body in the pose; the capsule never goes below the ground.
            y = Mathf.Max(y, Mathf.Min(_origin.y, _endY));
            Vector3 position = _origin + _forward * (s.x * _forwardScale);
            position.y = y;
            if (_collide)
            {
                // Slide: move with collisions, hugging the ground; stop if something blocks the way.
                Vector3 delta = position - transform.position;
                delta.y = -3f * dt;
                Vector3 before = transform.position;
                _cc.Move(delta);
                // Blocked (a wall, not just brushing a low roof): end the slide there.
                Vector3 moved = transform.position - before;
                moved.y = 0f;
                delta.y = 0f;
                if (p < 0.85f && delta.magnitude > 0.01f && moved.magnitude < delta.magnitude * 0.3f)
                {
                    Finish();
                    return;
                }
            }
            else transform.position = position;
            transform.rotation = Quaternion.Slerp(_startRotation, Quaternion.LookRotation(_forward),
                Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p / 0.15f)));

            if (p >= 1f) Finish();
        }

        private void Finish()
        {
            IsActive = false;
            _cooldown = Cooldown;
            if (_paramSet) SetParkourParam(0);
            _paramSet = false;
            if (_cc != null)
            {
                _cc.enabled = true;
                if (_collide) _cc.stepOffset = _stepOffset;
            }
            _collide = false;
            Physics.SyncTransforms();
            if (Controller != null) Controller.ExternallyDriven = false;
        }

        private void SetParkourParam(int value)
        {
            if (_mm != null && _mm.isActiveAndEnabled && _mm.TryGetControllerLayer(out AnimatorControllerPlayable layer))
                layer.SetInteger(ParkourId, value);
            else if (_animator != null && _animator.runtimeAnimatorController != null)
                _animator.SetInteger(ParkourId, value);
        }

        // ---------------------------------------------------------------- physics helpers

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }

        private bool SphereCast(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
        {
            int n = Physics.SphereCastNonAlloc(origin, ProbeRadius, dir, _hits, distance, Controller.GroundLayers,
                QueryTriggerInteraction.Ignore);
            return Nearest(n, out best);
        }

        private bool Raycast(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
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
    }
}
