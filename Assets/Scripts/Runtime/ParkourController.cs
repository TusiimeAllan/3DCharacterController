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
        [Tooltip("Climbs and hurdles start only this close to the obstacle's face (m); the recording's run-up is skipped to match")]
        public float TriggerDistance = 0.9f;
        [Tooltip("Jumping down from a ledge needs this much speed (m/s); slower, the character just steps off, so it can stand at edges")]
        public float DropRunSpeed = 3f;
        [Tooltip("...and the edge this close (m)")]
        public float DropTriggerDistance = 0.7f;

        [Header("Slide (sprint, then crouch)")]
        public bool EnableSlide = true;
        [Tooltip("Slowest speed a crouch press turns into a slide (m/s)")]
        public float MinSlideSpeed = 4.5f;
        [Tooltip("Capsule height while sliding, so it fits under low gaps")]
        public float SlideHeight = 1.0f;

        public bool IsActive { get; private set; }
        /// <summary>The move being performed, or null.</summary>
        public ParkourMove CurrentMove => IsActive ? _move : null;
        /// <summary>Progress through the current move, 0..1.</summary>
        public float Progress => IsActive && _move != null ? Mathf.Clamp01(_elapsed / Mathf.Max(0.05f, _move.Duration)) : 0f;
        /// <summary>True while the current move uses an edge the hands can hold (climbs, hurdles).</summary>
        public bool HasLedge { get; private set; }
        /// <summary>A point on the held edge (top of the wall face), the direction into the obstacle and its top.</summary>
        public Vector3 LedgePoint { get; private set; }
        public Vector3 LedgeFace { get; private set; }
        public float LedgeTop => LedgePoint.y;
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
            if (faceDist < 0.2f || faceDist > TriggerDistance) return; // not there yet: keep walking

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
            _pendingLedge = new Vector3(wall.point.x, top.point.y, wall.point.z);
            Begin(kind, pos, face, contact, hgt, endY);
        }

        private void TryDrop(Vector3 pos, Vector3 fwd)
        {
            Vector3 speed = _cc.velocity;
            speed.y = 0f;
            if (speed.magnitude < DropRunSpeed) return; // walking: step off (or stop at the edge)
            // Walk the ground ahead until it falls away.
            float edge = -1f;
            float groundY = 0f;
            float previousDrop = 0f;
            for (float d = 0.2f; d <= DropTriggerDistance; d += 0.1f)
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
                    if (WaterVolume.IsDeep(land.point, 1f)) return; // deep water below: just fall in and swim
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
            HasLedge = false;
            _origin = transform.position;
            _forward = Flat(v);
            _startRotation = transform.rotation;
            _endY = _origin.y;
            _elapsed = 0f;
            _s0 = Vector2.zero;
            _collide = true;
            IsActive = true;
            PlayState(move, move.WindowStart);

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

        /// <summary>
        /// Starts a move from where the character is now, ending at endY (e.g. climbing up from a ledge hang or
        /// off the top of a ladder). Returns false when the library has no move of that kind.
        /// </summary>
        public bool BeginFromHere(ParkourKind kind, Vector3 face, float endY, Vector3? ledgePoint = null)
        {
            if (Library == null || IsActive) return false;
            var options = new List<int>();
            for (int i = 0; i < Library.Moves.Count; i++)
                if (Library.Moves[i].Kind == kind && Library.Moves[i].Path != null && Library.Moves[i].Path.Length > 1) options.Add(i);
            if (options.Count == 0) return false;
            int index = options[Random.Range(0, options.Count)];
            _move = Library.Moves[index];
            HasLedge = ledgePoint.HasValue;
            if (HasLedge)
            {
                LedgePoint = ledgePoint.Value;
                LedgeFace = Flat(face);
            }
            _origin = transform.position;
            _forward = Flat(face);
            _startRotation = transform.rotation;
            _forwardScale = 1f;
            _heightScale = Mathf.Max(0.3f, (endY - _origin.y) / Mathf.Max(0.05f, _move.Height));
            _correctFrom = 0.4f;
            _unstretchFrom = 1f;
            if (_move.HasHold && ledgePoint.HasValue)
                FitHold(_move, ledgePoint.Value.y - _origin.y, Vector3.Dot(ledgePoint.Value - _origin, Flat(face)));
            _endY = endY;
            _elapsed = 0f;
            _s0 = Vector2.zero;
            _correction = Vector3.zero;
            _lastPathPosition = _origin;
            _collide = false;
            IsActive = true;
            PlayState(_move, _move.WindowStart);
            Controller.ExternallyDriven = true;
            return true;
        }

        private void Begin(ParkourKind kind, Vector3 pos, Vector3 face, float contactDistance, float height, float endY)
        {
            var options = new List<int>();
            for (int i = 0; i < Library.Moves.Count; i++)
                if (Library.Moves[i].Kind == kind && Library.Moves[i].Path != null && Library.Moves[i].Path.Length > 1) options.Add(i);
            if (options.Count == 0) return;
            int index = options[Random.Range(0, options.Count)];
            var move = Library.Moves[index];

            // Start at the point of the recording that is as far from its obstacle as we are from ours
            // (skipping the rest of its run-up), so nothing is stretched and the move begins right here.
            float startP = ProgressAtForward(move, move.ContactDistance - contactDistance);
            _s0 = move.Sample(startP);
            _forwardScale = 1f;
            _heightScale = height / Mathf.Max(0.05f, move.Height - _s0.y);
            _correctFrom = 0.4f;
            _unstretchFrom = 1f;
            if (move.HasHold && kind != ParkourKind.Hurdle)
                FitHold(move, height, Vector3.Dot(_pendingLedge - pos, face));

            _move = move;
            HasLedge = kind == ParkourKind.Hurdle || kind == ParkourKind.ClimbMedium || kind == ParkourKind.ClimbHigh;
            if (HasLedge)
            {
                LedgePoint = _pendingLedge;
                LedgeFace = face;
            }
            _origin = pos;
            _forward = face;
            _startRotation = transform.rotation;
            _endY = endY;
            _elapsed = startP * move.Duration;
            _correction = Vector3.zero;
            _lastPathPosition = pos;
            _collide = false;
            IsActive = true;
            PlayState(move, move.WindowStart + _elapsed);

            Controller.ExternallyDriven = true; // the path places the capsule, kept out of the geometry
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
            Vector2 s = _move.Sample(p) - _s0;
            Vector2 end = _move.Sample(1f) - _s0;
            float y = _origin.y + s.y * _heightScale;
            // Land exactly on the measured surface.
            float endError = _endY - (_origin.y + end.y * _heightScale);
            y += endError * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_correctFrom, 1f, p));
            // Crouches and rolls already lower the body in the pose; the capsule never goes below the ground.
            y = Mathf.Max(y, Mathf.Min(_origin.y, _endY));
            // The stretch that puts the hands on the edge applies up to the end of the hold; after it the
            // recording's own pace resumes (otherwise the step-off lunges forward).
            float forward;
            if (_unstretchFrom < 1f && p > _unstretchFrom)
            {
                float held = _move.Sample(_unstretchFrom).x - _s0.x;
                forward = held * _forwardScale + (s.x - held);
            }
            else forward = s.x * _forwardScale;
            Vector3 position = _origin + _forward * forward;
            position.y = y;
            if (_collide)
            {
                // Slide: move with collisions, hugging the ground; stop if something blocks the way.
                Vector3 delta = position - transform.position;
                delta.y = -3f * dt;
                // The recording stands up at the end; under a low roof, end here and let the crouch take over.
                if (p > 0.5f && Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up,
                        Controller.StandingHeight - 0.4f, Controller.GroundLayers, QueryTriggerInteraction.Ignore))
                {
                    Finish();
                    return;
                }
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
            else
            {
                // Follow the recorded path, but never sink into the obstacle (or anything else) on the way.
                // A correction is applied at once when needed, and eased off when no longer needed, so the body
                // never snaps forward the moment it clears an edge.
                Vector3 needed = Depenetrate(position) - position;
                _correction = needed.sqrMagnitude >= _correction.sqrMagnitude
                    ? needed
                    : Vector3.MoveTowards(_correction, needed, 1.5f * dt);
                Vector3 placed = Depenetrate(position + _correction);
                // Never move further in a frame than the recording itself moved, plus a little for corrections:
                // whatever the obstacle does to the path, the body can't teleport.
                float limit = (position - _lastPathPosition).magnitude + 2f * dt;
                transform.position = transform.position + Vector3.ClampMagnitude(placed - transform.position, limit);
                _lastPathPosition = position;
                Physics.SyncTransforms();
            }
            transform.rotation = Quaternion.Slerp(_startRotation, Quaternion.LookRotation(_forward),
                Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p / 0.15f)));

            if (p >= 1f) Finish();
        }

        /// <summary>Stops any move in progress at once (e.g. before a teleport).</summary>
        public void Cancel()
        {
            if (IsActive) Finish();
        }

        private Vector3 _pendingLedge;
        private Vector2 _s0; // recorded root offset where the move was started (run-up skipped)

        /// <summary>Progress at which the recorded path has gone this far forward (0 if never).</summary>
        private static float ProgressAtForward(ParkourMove move, float forward)
        {
            if (forward <= 0f || move.Path == null || move.Path.Length < 2) return 0f;
            for (int i = 1; i < move.Path.Length; i++)
            {
                if (move.Path[i].x < forward) continue;
                float a = move.Path[i - 1].x, b = move.Path[i].x;
                float t = b > a ? Mathf.InverseLerp(a, b, forward) : 0f;
                return Mathf.Clamp01((i - 1 + t) / (move.Path.Length - 1));
            }
            return 0f;
        }

        /// <summary>Cross-fades into the move's state at an exact clip time (so a shortened run-up matches).</summary>
        private void PlayState(ParkourMove move, float clipTime)
        {
            const float fade = 0.12f;
            if (_mm != null && _mm.isActiveAndEnabled && _mm.TryGetControllerLayer(out AnimatorControllerPlayable layer))
                layer.CrossFadeInFixedTime(move.StateName, fade, 0, clipTime);
            else if (_animator != null && _animator.runtimeAnimatorController != null)
                _animator.CrossFadeInFixedTime(move.StateName, fade, 0, clipTime);
        }
        private float _correctFrom = 0.4f;
        private float _unstretchFrom = 1f;
        private Vector3 _correction;
        private Vector3 _lastPathPosition;

        /// <summary>
        /// Stretches the recorded path so that, while the recording holds its edge, the hands are exactly on the
        /// real one. With h the root's recorded rise at that moment, H the real and Hr the recorded edge height,
        /// the vertical scale is 1 + (H - Hr) / h (and the same for the forward distance to the wall face).
        /// The correction that lands the feet on top only starts once the hands have let go.
        /// </summary>
        private void FitHold(ParkourMove move, float ledgeHeight, float faceDistance)
        {
            const float palm = 0.035f, inset = 0.09f;
            float rise = move.HoldRootRise - _s0.y, ahead = move.HoldRootForward - _s0.x;
            float handHeight = move.HoldHandHeight - _s0.y, handForward = move.HoldHandForward - _s0.x;
            if (rise > 0.05f)
                _heightScale = Mathf.Clamp((ledgeHeight + palm - handHeight + rise) / rise, 0.3f, 3f);
            if (ahead > 0.05f)
                _forwardScale = Mathf.Clamp((faceDistance + inset - handForward + ahead) / ahead, 0.2f, 3f);
            _correctFrom = Mathf.Clamp(move.HoldEnd, 0.4f, 0.95f);
            _unstretchFrom = Mathf.Clamp(move.HoldEnd, 0.2f, 1f);
        }

        private void Finish()
        {
            HasLedge = false;
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

        private readonly Collider[] _overlaps = new Collider[8];

        /// <summary>Moves a capsule position out of any colliders it overlaps (smallest push each).</summary>
        private Vector3 Depenetrate(Vector3 p)
        {
            // Below the top of the obstacle being climbed, the capsule stays in front of its face. (Pushed out of
            // the box's corner, the smallest way out flips between up and back, which reads as a teleport.)
            if (HasLedge && p.y < LedgeTop - 0.05f)
            {
                float into = Vector3.Dot(p - LedgePoint, LedgeFace);
                if (into < 0.15f && into + _cc.radius > 0f) p -= LedgeFace * (into + _cc.radius);
            }
            Quaternion rot = transform.rotation;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Vector3 center = p + rot * _cc.center;
                float half = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius);
                int n = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * half, center - Vector3.up * half, _cc.radius,
                    _overlaps, Controller.GroundLayers, QueryTriggerInteraction.Ignore);
                bool pushed = false;
                for (int i = 0; i < n; i++)
                {
                    var other = _overlaps[i];
                    if (other.transform.IsChildOf(transform)) continue;
                    if (Physics.ComputePenetration(_cc, p, rot, other, other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float dist) && dist > 0.0005f)
                    {
                        p += dir * (dist + 0.002f);
                        pushed = true;
                    }
                }
                if (!pushed) break;
            }
            return p;
        }

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
