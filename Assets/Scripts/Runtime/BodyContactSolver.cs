using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Makes animated bodies respect the world, after the animation has been evaluated each frame:
    ///
    /// 1. Body depenetration. During moves that place the body from a recording (parkour, rolls, landings,
    ///    slope slides, deaths) the torso, head and legs are tested as spheres against the level. The body is
    ///    pushed out by the deepest penetration (moving the hips moves the whole skeleton), so a roll never
    ///    sinks into the floor and a climb never puts the chest inside the wall.
    ///
    /// 2. Hand planting. While a climb or hurdle uses a ledge (ParkourController.HasLedge), each hand that
    ///    reaches near the edge is pinned to the closest point on the edge line and held there until the
    ///    recording lifts it away. The arm is re-solved with analytic two-bone IK (law of cosines) in the
    ///    plane the animation already bends the elbow in, so the pose keeps its character.
    ///
    /// Works through the Humanoid avatar, so it keeps working when CharacterSwitcher swaps models.
    /// </summary>
    [DefaultExecutionOrder(200)] // after animation, motion matching and the controllers have run
    [DisallowMultipleComponent]
    public class BodyContactSolver : MonoBehaviour
    {
        public bool EnableDepenetration = true;
        public bool EnableHandPlanting = true;

        [Header("References")]
        public ThirdPersonController Controller;
        public ParkourController Parkour;

        [Header("Depenetration")]
        [Tooltip("Largest correction applied in one frame (m)")]
        public float MaxPush = 0.6f;
        [Tooltip("How fast a correction eases out once it is no longer needed")]
        public float RecoverRate = 8f;

        [Header("Hand Planting")]
        [Tooltip("A hand closer than this to the edge is planted on it")]
        public float PlantReach = 0.5f;
        [Tooltip("A planted hand is released when the animation pulls it this far away")]
        public float ReleaseDistance = 0.55f;
        [Tooltip("How far onto the top surface the palm rests, from the edge")]
        public float EdgeInset = 0.09f;
        public float PlantInRate = 14f;
        public float PlantOutRate = 7f;
        [Tooltip("Part of the move during which hands may plant (progress 0..1)")]
        public Vector2 PlantWindow = new Vector2(0.08f, 0.85f);

        private static readonly (HumanBodyBones bone, float radius)[] BodyProbes =
        {
            (HumanBodyBones.Hips, 0.13f), (HumanBodyBones.Spine, 0.12f), (HumanBodyBones.Chest, 0.13f),
            (HumanBodyBones.Head, 0.1f), (HumanBodyBones.LeftUpperLeg, 0.08f), (HumanBodyBones.RightUpperLeg, 0.08f),
            (HumanBodyBones.LeftLowerLeg, 0.06f), (HumanBodyBones.RightLowerLeg, 0.06f),
            (HumanBodyBones.LeftFoot, 0.05f), (HumanBodyBones.RightFoot, 0.05f),
        };

        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private SwimController _swim;
        private ClimbController _climb;
        private CharacterActions _actions;
        private SphereCollider _probe;
        private readonly Collider[] _overlaps = new Collider[16];
        private Vector3 _push;

        private struct Hand
        {
            public HumanBodyBones Upper, Lower, End;
            public float Weight;
            public bool Planted;
            public Vector3 Target;
        }

        private Hand _left = new Hand { Upper = HumanBodyBones.LeftUpperArm, Lower = HumanBodyBones.LeftLowerArm, End = HumanBodyBones.LeftHand };
        private Hand _right = new Hand { Upper = HumanBodyBones.RightUpperArm, Lower = HumanBodyBones.RightLowerArm, End = HumanBodyBones.RightHand };

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
            _swim = GetComponent<SwimController>();
            _climb = GetComponent<ClimbController>();
            _actions = GetComponent<CharacterActions>();
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Parkour == null) Parkour = GetComponent<ParkourController>();

            // A hidden trigger sphere used only to measure penetration (never collides with anything).
            var go = new GameObject("BodyProbe") { hideFlags = HideFlags.HideInHierarchy };
            go.transform.SetParent(transform, false);
            go.layer = 2; // Ignore Raycast
            _probe = go.AddComponent<SphereCollider>();
            _probe.isTrigger = true;
            _probe.radius = 0.01f; // only its shape is used, at positions passed to ComputePenetration
        }

        private void LateUpdate()
        {
            if (_animator == null || !_animator.isHuman || Controller == null) return;
            float dt = Time.deltaTime;

            if (EnableDepenetration) Depenetrate(dt);
            if (EnableHandPlanting)
            {
                bool ledge = Parkour != null && Parkour.IsActive && Parkour.HasLedge;
                var move = ledge ? Parkour.CurrentMove : null;
                float p = ledge ? Parkour.Progress : 0f;
                SolveHand(ref _left, ledge, ContactWeight(move != null ? move.LeftHandContact : Vector2.zero, p), p, dt);
                SolveHand(ref _right, ledge, ContactWeight(move != null ? move.RightHandContact : Vector2.zero, p), p, dt);
            }
        }

        // ---------------------------------------------------------------- depenetration

        private bool PosedFromRecording()
        {
            if (_swim != null && _swim.IsSwimming) return false; // floating: water, not floors
            if (_climb != null && _climb.IsClimbing) return false; // hands/feet meet the wall on purpose
            return Controller.ExternallyDriven || Controller.IsSlidingOnSlope ||
                   (_mm != null && _mm.InLandingState) ||
                   (_actions != null && _actions.isActiveAndEnabled && _actions.IsDead);
        }

        private void Depenetrate(float dt)
        {
            Vector3 wanted = Vector3.zero;
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null && PosedFromRecording())
            {
                // Deepest push up, and the strongest horizontal push, over all probes.
                float up = 0f;
                Vector3 side = Vector3.zero;
                foreach (var (bone, radius) in BodyProbes)
                {
                    var t = _animator.GetBoneTransform(bone);
                    if (t == null) continue;
                    Vector3 p = t.position;
                    // Floors and tops from above: a probe under a surface is pushed up onto it (a penetration
                    // test would push a probe already below a thin floor out through the bottom).
                    if (Physics.Raycast(new Vector3(p.x, p.y + 0.6f, p.z), Vector3.down, out RaycastHit floor,
                            0.6f + radius, Controller.GroundLayers, QueryTriggerInteraction.Ignore) &&
                        !floor.transform.IsChildOf(transform) && Vector3.Angle(floor.normal, Vector3.up) < 50f)
                        up = Mathf.Max(up, floor.point.y + radius - p.y);
                    _probe.radius = radius;
                    int n = Physics.OverlapSphereNonAlloc(p, radius, _overlaps, Controller.GroundLayers,
                        QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        var other = _overlaps[i];
                        if (other == null || other.transform.IsChildOf(transform)) continue;
                        if (!Physics.ComputePenetration(_probe, p, Quaternion.identity, other,
                                other.transform.position, other.transform.rotation, out Vector3 dir, out float dist)) continue;
                        Vector3 v = dir * dist;
                        Vector3 h = new Vector3(v.x, 0f, v.z); // walls only: floors are handled from above
                        if (h.sqrMagnitude > side.sqrMagnitude) side = h;
                    }
                }
                wanted = Vector3.ClampMagnitude(side + Vector3.up * up, MaxPush);
            }

            // Push out at once (never show the overlap), ease back when it clears.
            for (int axis = 0; axis < 3; axis++)
            {
                float w = wanted[axis], c = _push[axis];
                bool grow = Mathf.Abs(w) > Mathf.Abs(c) && (Mathf.Sign(w) == Mathf.Sign(c) || Mathf.Abs(c) < 1e-4f);
                _push[axis] = grow ? w : Mathf.Lerp(c, w, 1f - Mathf.Exp(-RecoverRate * dt));
            }
            if (_push.sqrMagnitude > 1e-8f && hips != null) hips.position += _push;
        }

        // ---------------------------------------------------------------- hands

        /// <summary>1 inside the recorded hold window, easing in and out over its edges; -1 if the move has none.</summary>
        private static float ContactWeight(Vector2 window, float p)
        {
            if (window.y <= window.x) return -1f;
            const float ease = 0.06f;
            float inW = Mathf.InverseLerp(window.x - ease, window.x + ease, p);
            float outW = 1f - Mathf.InverseLerp(window.y - ease, window.y + ease, p);
            return Mathf.Clamp01(Mathf.Min(inW, outW));
        }

        private void SolveHand(ref Hand hand, bool ledgeActive, float contact, float progress, float dt)
        {
            var upper = _animator.GetBoneTransform(hand.Upper);
            var lower = _animator.GetBoneTransform(hand.Lower);
            var end = _animator.GetBoneTransform(hand.End);
            if (upper == null || lower == null || end == null) return;
            Vector3 animated = end.position;

            if (ledgeActive && contact >= 0f)
            {
                // The recording says when this hand holds the edge: plant it there, on the real edge.
                Vector3 edgeFace = Parkour.LedgeFace;
                Vector3 edgeTangent = Vector3.Cross(Vector3.up, edgeFace).normalized;
                Vector3 edgePoint = Parkour.LedgePoint;
                if (contact > 0f && !hand.Planted)
                {
                    Vector3 onLine = edgePoint + edgeTangent * Vector3.Dot(animated - edgePoint, edgeTangent);
                    hand.Target = onLine + edgeFace * EdgeInset + Vector3.up * 0.035f;
                    hand.Planted = true;
                }
                if (contact <= 0f) hand.Planted = false;
                hand.Weight = contact;
                if (hand.Weight > 0.001f) TwoBoneIK(upper, lower, end, Vector3.Lerp(animated, hand.Target, hand.Weight));
                return;
            }
            if (ledgeActive && progress >= PlantWindow.x && progress <= PlantWindow.y)
            {
                Vector3 face = Parkour.LedgeFace;
                Vector3 tangent = Vector3.Cross(Vector3.up, face).normalized;
                Vector3 edge = Parkour.LedgePoint;
                // Closest point on the edge line, then a palm's width onto the top surface.
                Vector3 onEdge = edge + tangent * Vector3.Dot(animated - edge, tangent);
                Vector3 target = onEdge + face * EdgeInset + Vector3.up * 0.035f;

                if (!hand.Planted)
                {
                    // Plant when the hand comes up to the edge, from the near side, and is not already past it.
                    bool near = (animated - target).magnitude < PlantReach;
                    bool beside = Vector3.Dot(animated - edge, face) < 0.45f;
                    bool high = animated.y > Parkour.LedgeTop - 0.45f;
                    if (near && beside && high)
                    {
                        hand.Planted = true;
                        hand.Target = target;
                    }
                }
                else if ((animated - hand.Target).magnitude > ReleaseDistance)
                {
                    hand.Planted = false; // the recording has moved the hand on
                }
            }
            else hand.Planted = false;
            if (!ledgeActive) hand.Planted = false;

            hand.Weight = Mathf.MoveTowards(hand.Weight, hand.Planted ? 1f : 0f,
                (hand.Planted ? PlantInRate : PlantOutRate) * dt);
            if (hand.Weight <= 0.001f) return;
            TwoBoneIK(upper, lower, end, Vector3.Lerp(animated, hand.Target, hand.Weight));
        }

        /// <summary>
        /// Analytic two-bone IK: places the end of the chain at target, keeping the bend plane of the current pose.
        /// Law of cosines gives the shoulder angle; the elbow follows.
        /// </summary>
        public static void TwoBoneIK(Transform root, Transform mid, Transform end, Vector3 target)
        {
            Vector3 a = root.position, b = mid.position, c = end.position;
            float upperLen = (b - a).magnitude, lowerLen = (c - b).magnitude;
            if (upperLen < 1e-4f || lowerLen < 1e-4f) return;

            Vector3 toTarget = target - a;
            float dist = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upperLen - lowerLen) + 1e-3f, upperLen + lowerLen - 1e-3f);
            Vector3 dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : (c - a).normalized;

            // Bend plane from the current pose (falls back to something sensible when the arm is straight).
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-8f) normal = Vector3.Cross(dir, Vector3.up);
            if (normal.sqrMagnitude < 1e-8f) normal = Vector3.Cross(dir, Vector3.forward);
            normal.Normalize();

            float cosAngle = (upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist);
            float angle = Mathf.Acos(Mathf.Clamp(cosAngle, -1f, 1f)) * Mathf.Rad2Deg;
            // Two elbow solutions; keep the one on the side the animation bends to.
            Vector3 elbowA = a + Quaternion.AngleAxis(angle, normal) * dir * upperLen;
            Vector3 elbowB = a + Quaternion.AngleAxis(-angle, normal) * dir * upperLen;
            Vector3 elbow = (elbowA - b).sqrMagnitude <= (elbowB - b).sqrMagnitude ? elbowA : elbowB;

            Quaternion endRotation = end.rotation; // keep the hand's own orientation
            root.rotation = Quaternion.FromToRotation(b - a, elbow - a) * root.rotation;
            Vector3 midNow = mid.position, endNow = end.position;
            mid.rotation = Quaternion.FromToRotation(endNow - midNow, target - midNow) * mid.rotation;
            end.rotation = endRotation;
        }
    }
}
