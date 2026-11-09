using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Ledges and ladders.
    /// Ledge: in the air, a ledge within hand reach in front of the character is grabbed. While hanging,
    /// left/right shimmies along it, forward or Jump climbs up (ParkourController, ClimbFromHang), back or
    /// Crouch lets go.
    /// Ladder (see Ladder): walking into one attaches to it. Forward/back climbs, Jump lets go; the top hauls
    /// the character onto the platform, the bottom steps off.
    /// The controller stays in charge of the capsule. Because the Mixamo clips were recorded with offsets,
    /// the hands are measured every frame and the capsule is nudged so they meet the ledge or the ladder.
    /// </summary>
    [DefaultExecutionOrder(-5)] // after ParkourController, before ThirdPersonController clears the jump input
    [DisallowMultipleComponent]
    public class ClimbController : MonoBehaviour
    {
        public bool EnableLedges = true;
        public bool EnableLadders = true;

        [Header("References")]
        public ThirdPersonController Controller;
        public ParkourController Parkour;

        [Header("Ledges")]
        [Tooltip("Ledge heights above the feet that the hands can grab while airborne")]
        public float MinGrabHeight = 1.5f;
        public float MaxGrabHeight = 2.4f;
        [Tooltip("First guess of the hands' height above the feet while hanging (corrected by measuring)")]
        public float HangHandHeight = 2.25f;
        [Tooltip("Capsule centre distance from the wall while hanging")]
        public float WallDistance = 0.32f;
        public float ShimmySpeed = 0.5f;

        [Header("Ladders")]
        [Tooltip("Climbing speed at full input (m/s)")]
        public float LadderSpeed = 0.8f;
        [Tooltip("Ground speed of the ladder clip at normal playback (m/s), to sync the animation")]
        public float LadderClipSpeed = 0.75f;
        [Tooltip("Capsule centre distance from the ladder face")]
        public float LadderDistance = 0.4f;
        [Tooltip("Leave the ladder over the top once the feet are this far below its top")]
        public float LadderTopExit = 1.9f;

        [Header("Hand Alignment")]
        [Tooltip("How fast the capsule is nudged so the hands meet the ledge or ladder")]
        public float AlignRate = 10f;

        public enum State { None, Hanging, Ladder }
        public State Current { get; private set; }
        public bool IsClimbing => Current != State.None;

        private static readonly int HangingId = Animator.StringToHash("Hanging");
        private static readonly int HangMoveId = Animator.StringToHash("HangMove");
        private static readonly int OnLadderId = Animator.StringToHash("OnLadder");
        private static readonly int LadderSpeedId = Animator.StringToHash("LadderSpeed");

        private PlayerInputs _inputs;
        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private CharacterController _cc;
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private Vector3 _edge;          // point on the ledge edge the hands hold
        private Vector3 _face;          // horizontal direction into the wall / ladder
        private float _ledgeTop;
        private Ladder _ladder;
        private float _ladderY;
        private Vector3 _align;         // measured correction so the hands meet their target
        private float _cooldown;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Parkour == null) Parkour = GetComponent<ParkourController>();
            _inputs = GetComponent<PlayerInputs>();
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
            _cc = GetComponent<CharacterController>();
        }

        private void OnDisable()
        {
            if (IsClimbing) Detach(Vector3.zero);
        }

        private void Update()
        {
            if (Controller == null || _inputs == null) return;
            switch (Current)
            {
                case State.Hanging: UpdateHang(Time.deltaTime); return;
                case State.Ladder: UpdateLadder(Time.deltaTime); return;
            }

            if (_cooldown > 0f)
            {
                _cooldown -= Time.deltaTime;
                return;
            }
            if (Controller.ExternallyDriven || Controller.MovementLocked || (Parkour != null && Parkour.IsActive)) return;
            if (EnableLadders && TryLadder()) return;
            if (EnableLedges && !Controller.Grounded) TryGrab();
        }

        // Hands are only known after the animation has been evaluated.
        private void LateUpdate()
        {
            if (!IsClimbing || _animator == null || !_animator.isHuman) return;
            var lh = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rh = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (lh == null || rh == null) return;
            Vector3 hands = (lh.position + rh.position) * 0.5f;

            Vector3 error;
            if (Current == State.Hanging)
            {
                // Up/down and along the ledge only: the body keeps its distance from the wall.
                error = new Vector3(_edge.x, _ledgeTop + 0.03f, _edge.z) - hands;
                error -= _face * Vector3.Dot(error, _face);
            }
            else
            {
                // Ladder: hands in the ladder's plane and centred on it; height is up to the player.
                Vector3 target = _ladder.Bottom;
                Vector3 offset = target - hands;
                Vector3 right = Vector3.Cross(Vector3.up, _ladder.Out);
                error = _ladder.Out * Vector3.Dot(offset, _ladder.Out) + right * Vector3.Dot(offset, right);
            }
            _align += error * (1f - Mathf.Exp(-AlignRate * Time.deltaTime));
            _align = Vector3.ClampMagnitude(_align, 1.5f);
        }

        // ---------------------------------------------------------------- ledges

        private void TryGrab()
        {
            Vector3 pos = transform.position;
            Vector3 fwd = Flat(transform.forward);
            if (!SphereCast(pos + Vector3.up * 1.3f, 0.15f, fwd, 0.7f, out RaycastHit wall)) return;
            if (wall.rigidbody != null && !wall.rigidbody.isKinematic) return;
            Vector3 face = Flat(-wall.normal);
            if (face == Vector3.zero || Vector3.Angle(face, fwd) > 50f) return;

            Vector3 over = new Vector3(wall.point.x, 0f, wall.point.z) + face * 0.15f;
            float startY = pos.y + MaxGrabHeight + 0.3f;
            if (!Raycast(new Vector3(over.x, startY, over.z), Vector3.down, MaxGrabHeight - MinGrabHeight + 0.3f,
                    out RaycastHit top) || top.distance < 0.01f) return; // no ledge in reach (or the wall continues)
            if (Vector3.Angle(top.normal, Vector3.up) > 30f) return;
            float height = top.point.y - pos.y;
            if (height < MinGrabHeight || height > MaxGrabHeight) return;

            _face = face;
            _ledgeTop = top.point.y;
            _edge = new Vector3(wall.point.x, _ledgeTop, wall.point.z);
            _align = Vector3.zero;
            Current = State.Hanging;
            Controller.ExternallyDriven = true;
            transform.rotation = Quaternion.LookRotation(_face);
            PlaceHanging();
            SetBool(HangingId, true);
            SetFloat(HangMoveId, 0f);
        }

        private void UpdateHang(float dt)
        {
            Controller.ExternallyDriven = true;
            Vector2 move = _inputs.move;

            if (_inputs.jump || move.y > 0.5f)
            {
                _inputs.jump = false;
                ClimbUp();
                return;
            }
            if (move.y < -0.5f || _inputs.crouch)
            {
                _inputs.crouch = false;
                Detach(-_face * 0.15f);
                return;
            }

            float side = Mathf.Abs(move.x) > 0.2f ? Mathf.Sign(move.x) * Mathf.Clamp01(Mathf.Abs(move.x)) : 0f;
            if (side != 0f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, _face);
                Vector3 dir = right * Mathf.Sign(side);
                Vector3 next = _edge + dir * (ShimmySpeed * Mathf.Abs(side) * dt);
                if (LedgeContinues(next + dir * 0.35f) && !BlockedSideways(dir)) _edge = next;
                else side = 0f;
            }
            SetFloat(HangMoveId, side);
            PlaceHanging();
        }

        private void PlaceHanging()
        {
            transform.position = _edge - _face * WallDistance - Vector3.up * HangHandHeight + _align;
            transform.rotation = Quaternion.LookRotation(_face);
            Physics.SyncTransforms();
        }

        private bool LedgeContinues(Vector3 edgePoint)
        {
            Vector3 over = edgePoint + _face * 0.15f;
            if (!Raycast(new Vector3(over.x, _ledgeTop + 0.3f, over.z), Vector3.down, 0.5f, out RaycastHit top) ||
                Mathf.Abs(top.point.y - _ledgeTop) > 0.15f) return false;
            // ... and there is still a wall to hang against.
            Vector3 under = edgePoint - _face * 0.4f;
            return Raycast(new Vector3(under.x, _ledgeTop - 0.3f, under.z), _face, 0.7f, out _);
        }

        private bool BlockedSideways(Vector3 dir)
        {
            Vector3 body = transform.position + Vector3.up * (HangHandHeight - 0.6f);
            return SphereCast(body, 0.2f, dir, 0.45f, out _);
        }

        private void ClimbUp()
        {
            // Room to stand on top?
            Vector3 stand = _edge + _face * 0.5f;
            Vector3 bottom = new Vector3(stand.x, _ledgeTop + _cc.radius + 0.05f, stand.z);
            bool room = !Physics.CheckCapsule(bottom, bottom + Vector3.up * (Controller.StandingHeight - 2f * _cc.radius),
                _cc.radius * 0.9f, Controller.GroundLayers, QueryTriggerInteraction.Ignore);
            if (!room) return; // keep hanging

            Current = State.None;
            SetBool(HangingId, false);
            SetFloat(HangMoveId, 0f);
            if (Parkour == null || !Parkour.BeginFromHere(ParkourKind.ClimbFromHang, _face, _ledgeTop,
                    new Vector3(_edge.x, _ledgeTop, _edge.z)))
                Detach(-_face * 0.15f);
        }

        // ---------------------------------------------------------------- ladders

        private bool TryLadder()
        {
            Vector3 pos = transform.position;
            if (Controller.InputMagnitude < 0.3f) return false;
            // Where the stick points in the world (camera-relative), not just where the body faces, so
            // backing away from a ladder you just stepped off does not grab it again.
            Vector3 fwd = Flat(transform.forward);
            if (Controller.CameraTransform != null)
                fwd = Flat(Quaternion.Euler(0f, Controller.CameraTransform.eulerAngles.y, 0f) *
                           new Vector3(_inputs.move.x, 0f, _inputs.move.y));
            foreach (var ladder in Ladder.All)
            {
                Vector3 rel = pos - ladder.Bottom;
                Vector3 right = Vector3.Cross(Vector3.up, ladder.Out);
                float lateral = Vector3.Dot(rel, right);
                if (Mathf.Abs(lateral) > ladder.HalfWidth + 0.2f) continue;
                float front = Vector3.Dot(rel, ladder.Out);
                float h = pos.y - ladder.Bottom.y;

                // From the bottom (or anywhere along it): facing the ladder, just in front of it.
                // Needs forward on the stick too: that is what climbs, so holding "down" never re-grabs it.
                if (front > -0.05f && front < 0.9f && h > -0.3f && h < ladder.Height - LadderTopExit &&
                    Vector3.Dot(fwd, -ladder.Out) > 0.6f && _inputs.move.y > 0.3f)
                {
                    Attach(ladder, Mathf.Max(h, 0f) + ladder.Bottom.y);
                    return true;
                }
                // From the top: walking off the platform edge towards the ladder.
                if (front > -0.9f && front < 0.2f && Mathf.Abs(h - ladder.Height) < 0.35f &&
                    Vector3.Dot(fwd, ladder.Out) > 0.6f && Controller.Grounded)
                {
                    Attach(ladder, ladder.Top.y - LadderTopExit - 0.05f);
                    return true;
                }
            }
            return false;
        }

        private void Attach(Ladder ladder, float y)
        {
            _ladder = ladder;
            _face = -ladder.Out;
            _ladderY = y;
            _align = Vector3.zero;
            Current = State.Ladder;
            Controller.ExternallyDriven = true;
            PlaceOnLadder();
            SetBool(OnLadderId, true);
            SetFloat(LadderSpeedId, 0f);
        }

        private void UpdateLadder(float dt)
        {
            Controller.ExternallyDriven = true;
            if (_inputs.jump)
            {
                _inputs.jump = false;
                Detach(_ladder.Out * 0.35f);
                return;
            }

            float v = Mathf.Clamp(_inputs.move.y, -1f, 1f) * LadderSpeed;
            _ladderY += v * dt;
            SetFloat(LadderSpeedId, v / Mathf.Max(0.1f, LadderClipSpeed));

            if (v < 0f && _ladderY <= _ladder.Bottom.y)
            {
                _ladderY = _ladder.Bottom.y;
                PlaceOnLadder();
                Detach(_ladder.Out * 0.1f);
                return;
            }
            if (v > 0f && _ladderY >= _ladder.Top.y - LadderTopExit)
            {
                Current = State.None;
                SetBool(OnLadderId, false);
                SetFloat(LadderSpeedId, 0f);
                if (Parkour == null || !Parkour.BeginFromHere(ParkourKind.ClimbFromHang, _face, _ladder.Top.y, _ladder.Top))
                    Detach(Vector3.zero);
                return;
            }
            PlaceOnLadder();
        }

        private void PlaceOnLadder()
        {
            Vector3 p = _ladder.Bottom + _ladder.Out * LadderDistance + _align;
            p.y = _ladderY;
            // Never closer to the ladder face than the capsule radius.
            float front = Vector3.Dot(p - _ladder.Bottom, _ladder.Out);
            float min = (_cc != null ? _cc.radius : 0.28f) + 0.02f;
            if (front < min) p += _ladder.Out * (min - front);
            transform.position = p;
            transform.rotation = Quaternion.LookRotation(_face);
            Physics.SyncTransforms();
        }

        // ---------------------------------------------------------------- shared

        private void Detach(Vector3 push)
        {
            if (Current == State.Hanging) SetBool(HangingId, false);
            if (Current == State.Ladder) SetBool(OnLadderId, false);
            SetFloat(HangMoveId, 0f);
            SetFloat(LadderSpeedId, 0f);
            Current = State.None;
            transform.position += push;
            Physics.SyncTransforms();
            if (Controller != null) Controller.ExternallyDriven = false;
            _cooldown = 0.5f;
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

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }

        private bool SphereCast(Vector3 origin, float radius, Vector3 dir, float distance, out RaycastHit best)
        {
            int n = Physics.SphereCastNonAlloc(origin, radius, dir, _hits, distance, Controller.GroundLayers,
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
