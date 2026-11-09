using Transport;
using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Seats the player on a <see cref="RideableVehicle"/> that stays where it is (the showroom): hips on the
    /// seat, hands wrapped round the grips, feet on the pedals or pegs. A/D turn the handlebars and the hands
    /// follow the grips. Locomotion is held off while riding (Controller.ExternallyDriven) and the Animator
    /// Controller plays the "Ride" state (seated pose) that IK then fits to the vehicle.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    [RequireComponent(typeof(Animator))]
    public class BikeRider : MonoBehaviour
    {
        public ThirdPersonController Controller;
        public PlayerInputs Inputs;

        [Header("Fit")]
        [Tooltip("Hips sit this far above the seat socket")]
        public float HipOffset = 0.06f;
        [Tooltip("Forward lean of the upper body per vehicle kind (bicycle, motorbike, scooter)")]
        public Vector3 Lean = new Vector3(18f, 26f, 4f);
        [Tooltip("Arms end this straight at the grips (1 = locked elbows); the lean adjusts until they do")]
        [Range(0.7f, 1f)] public float ArmReach = 0.93f;
        [Tooltip("How much of the steering the shoulders turn with")]
        [Range(0f, 1f)] public float BodySteerFollow = 0.6f;

        [Header("Steering")]
        public float SteerSpeed = 120f;
        [Tooltip("Steering reached at full A/D, as a share of the vehicle's own limit")]
        [Range(0f, 1f)] public float SteerRange = 0.6f;

        [Header("Hands")]
        [Tooltip("Finger curl round the grip, degrees per joint (proximal, middle, distal)")]
        public Vector3 FingerCurl = new Vector3(70f, 80f, 50f);
        public Vector3 ThumbCurl = new Vector3(15f, 25f, 25f);

        public RideableVehicle Vehicle { get; private set; }
        public bool IsRiding => Vehicle != null;

        private static readonly int RidingId = Animator.StringToHash("Riding");
        private Animator _animator;
        private MotionMatchingLocomotion _mm;
        private Behaviour[] _paused;
        private float _steer;
        private float _autoLean;
        private Vector3 _wristL, _wristR;
        private int _ikFrame = -1;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _mm = GetComponent<MotionMatchingLocomotion>();
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Inputs == null) Inputs = GetComponent<PlayerInputs>();
            // Components that place feet or push the body out of geometry; the seat does that while riding.
            _paused = new Behaviour[] { GetComponent<HumanoidGrounding>(), GetComponent<BodyContactSolver>() };
        }

        public void Mount(RideableVehicle vehicle)
        {
            if (vehicle == null) return;
            if (!IsRiding)
                foreach (var b in _paused) if (b != null) b.enabled = false;
            if (Vehicle != vehicle) _autoLean = 0f;
            Vehicle = vehicle;
            _steer = 0f;
            Controller.ExternallyDriven = true;
            transform.rotation = Quaternion.LookRotation(Flat(vehicle.transform.forward), Vector3.up);
            PlaceOnSeat();
            SetRiding(true);
        }

        /// <summary>Gets off on the vehicle's left (or the given spot) and hands back to locomotion.</summary>
        public void Dismount()
        {
            if (!IsRiding) return;
            var v = Vehicle;
            Vehicle = null;
            Vector3 spot = v.mountL != null ? v.mountL.position : v.transform.position - v.transform.right * 0.6f;
            transform.SetPositionAndRotation(new Vector3(spot.x, v.transform.position.y, spot.z),
                Quaternion.LookRotation(Flat(v.transform.forward), Vector3.up));
            Physics.SyncTransforms();
            v.steerAngle = 0f;
            SetRiding(false);
            Controller.ExternallyDriven = false;
            foreach (var b in _paused) if (b != null) b.enabled = true;
        }

        private void Update()
        {
            if (!IsRiding) return;
            Controller.ExternallyDriven = true;
            SetRiding(true); // a character switch rebinds the Animator and clears parameters

            float target = Inputs != null ? Inputs.move.x * Vehicle.maxSteerAngle * SteerRange : 0f;
            _steer = Mathf.MoveTowards(_steer, target, SteerSpeed * Time.deltaTime);
            Vehicle.steerAngle = _steer;
        }

        private void LateUpdate()
        {
            if (!IsRiding) return;
            PlaceOnSeat();
            FitLean();
            GripHand(HumanBodyBones.LeftHand, Vehicle.handL, true);
            GripHand(HumanBodyBones.RightHand, Vehicle.handR, false);
        }

        // ---------------------------------------------------------------- body

        /// <summary>Moves the root so the animated hips land on the seat (the vehicle never moves, so this settles at once).</summary>
        private void PlaceOnSeat()
        {
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || Vehicle.seat == null) return;
            Vector3 target = Vehicle.seat.position + Vehicle.seat.up * HipOffset;
            transform.position += target - hips.position;
            transform.rotation = Quaternion.LookRotation(Flat(Vehicle.transform.forward), Vector3.up);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (!IsRiding || !_animator.isHuman) return;
            if (_ikFrame == Time.frameCount) return; // playable graphs can call this more than once a frame
            _ikFrame = Time.frameCount;

            var v = Vehicle.transform;
            float lean = Vehicle.kind == RideableVehicle.Kind.Bicycle ? Lean.x
                       : Vehicle.kind == RideableVehicle.Kind.Motorbike ? Lean.y : Lean.z;
            _animator.bodyRotation = Quaternion.AngleAxis(_steer * BodySteerFollow, v.up) *
                                     Quaternion.AngleAxis(lean + _autoLean, v.right) * _animator.bodyRotation;

            PinHand(AvatarIKGoal.LeftHand, Vehicle.handL, true);
            PinHand(AvatarIKGoal.RightHand, Vehicle.handR, false);
            PinFoot(AvatarIKGoal.LeftFoot, Vehicle.footL, _animator.leftFeetBottomHeight);
            PinFoot(AvatarIKGoal.RightFoot, Vehicle.footR, _animator.rightFeetBottomHeight);

            // Knees forward and a little out, elbows out and down.
            Hint(AvatarIKHint.LeftKnee, Weight(Vehicle.footL), v.TransformPoint(new Vector3(-0.18f, 0.75f, 0.45f)));
            Hint(AvatarIKHint.RightKnee, Weight(Vehicle.footR), v.TransformPoint(new Vector3(0.18f, 0.75f, 0.45f)));
            Hint(AvatarIKHint.LeftElbow, Weight(Vehicle.handL), v.TransformPoint(new Vector3(-0.55f, 0.9f, -0.2f)));
            Hint(AvatarIKHint.RightElbow, Weight(Vehicle.handR), v.TransformPoint(new Vector3(0.55f, 0.9f, -0.2f)));
        }

        private static float Weight(Transform t) => t != null ? 1f : 0f;

        private void Hint(AvatarIKHint hint, float weight, Vector3 position)
        {
            _animator.SetIKHintPositionWeight(hint, weight);
            _animator.SetIKHintPosition(hint, position);
        }

        private void PinFoot(AvatarIKGoal goal, Transform socket, float soleHeight)
        {
            if (socket == null) { _animator.SetIKPositionWeight(goal, 0f); _animator.SetIKRotationWeight(goal, 0f); return; }
            var v = Vehicle.transform;
            // The ball of the foot on the pedal, so the ankle sits above and a little behind it.
            Vector3 ankle = socket.position + v.up * soleHeight - v.forward * 0.07f;
            _animator.SetIKPositionWeight(goal, 1f);
            _animator.SetIKPosition(goal, ankle);
            _animator.SetIKRotationWeight(goal, 1f);
            _animator.SetIKRotation(goal, Quaternion.LookRotation(v.forward, v.up));
        }

        // ---------------------------------------------------------------- hands

        /// <summary>The way the fingers point and the way the palm faces when wrapped round this grip.</summary>
        private void GripFrame(Transform grip, bool left, out Vector3 fingers, out Vector3 palm)
        {
            Transform frame = Vehicle.steer != null ? Vehicle.steer : Vehicle.transform;
            Vector3 fwd = Flat(Vehicle.transform.forward);
            Quaternion steer = Quaternion.AngleAxis(_steer, Vector3.up);
            Vector3 outward = steer * (Vehicle.transform.right * (left ? -1f : 1f));
            fwd = steer * fwd;
            palm = (-Vector3.up * 0.85f - fwd * 0.35f).normalized;
            fingers = Vector3.ProjectOnPlane(fwd + outward * 0.2f, palm).normalized;
        }

        private void PinHand(AvatarIKGoal goal, Transform grip, bool left)
        {
            if (grip == null) { _animator.SetIKPositionWeight(goal, 0f); _animator.SetIKRotationWeight(goal, 0f); return; }
            GripFrame(grip, left, out Vector3 fingers, out Vector3 palm);
            float handLength = HandLength(left);
            // The grip lies across the palm, so the wrist sits behind it and on the back-of-hand side.
            Vector3 wrist = grip.position - fingers * handLength * 0.55f - palm * 0.03f;
            if (left) _wristL = wrist; else _wristR = wrist;
            _animator.SetIKPositionWeight(goal, 1f);
            _animator.SetIKPosition(goal, wrist);
            _animator.SetIKRotationWeight(goal, 0f); // the hand is turned onto the grip after IK, from its real bones
        }

        /// <summary>
        /// Leans the upper body further forward (or back) until the arms reach the grips with a slight bend:
        /// long reaches (a sport bike) fold the rider down, short ones (a scooter) sit them up.
        /// </summary>
        private void FitLean()
        {
            // Both arms on average: turning the bars brings one grip closer and pushes the other away,
            // and the shoulders turn with them instead of the rider diving at the far grip.
            float sum = 0f;
            int n = 0;
            if (Vehicle.handL != null) { float e = ReachError(true); if (e > float.MinValue) { sum += e; n++; } }
            if (Vehicle.handR != null) { float e = ReachError(false); if (e > float.MinValue) { sum += e; n++; } }
            if (n == 0) return;
            _autoLean = Mathf.Clamp(_autoLean + Mathf.Clamp(sum / n * 400f, -40f, 40f) * Time.deltaTime, -15f, 40f);
        }

        private float ReachError(bool left)
        {
            var upper = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var lower = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (upper == null || lower == null || hand == null) return float.MinValue;
            float arm = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
            return Vector3.Distance(upper.position, left ? _wristL : _wristR) - arm * ArmReach;
        }

        private float HandLength(bool left)
        {
            var hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var middle = _animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            return hand != null && middle != null ? Vector3.Distance(hand.position, middle.position) : 0.09f;
        }

        /// <summary>Turns the hand palm-down onto the grip and closes the fingers round it.</summary>
        private void GripHand(HumanBodyBones handBone, Transform grip, bool left)
        {
            var hand = _animator.GetBoneTransform(handBone);
            if (hand == null || grip == null) return;
            var index = _animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var middle = _animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var little = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            if (index == null || middle == null || little == null) return;

            // Where the hand points and faces now (rig independent: measured from its bones).
            Vector3 curFingers = (middle.position - hand.position).normalized;
            Vector3 across = (index.position - little.position).normalized;
            Vector3 curPalm = (left ? Vector3.Cross(across, curFingers) : Vector3.Cross(curFingers, across)).normalized;
            GripFrame(grip, left, out Vector3 fingers, out Vector3 palm);
            Quaternion turn = Quaternion.LookRotation(fingers, -palm) * Quaternion.Inverse(Quaternion.LookRotation(curFingers, -curPalm));
            hand.rotation = turn * hand.rotation;

            // Close each finger towards the palm (which now faces the grip), joint by joint.
            Curl(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal, palm, FingerCurl);
            Curl(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal, palm, FingerCurl);
            Curl(left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal, palm, FingerCurl);
            Curl(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal, palm, FingerCurl);
            Curl(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal, palm, ThumbCurl);
        }

        private void Curl(HumanBodyBones proximal, Vector3 palm, Vector3 angles)
        {
            var bone = _animator.GetBoneTransform(proximal);
            for (int joint = 0; joint < 3 && bone != null; joint++)
            {
                Transform child = bone.childCount > 0 ? bone.GetChild(0) : null;
                if (child == null) break;
                Vector3 dir = (child.position - bone.position).normalized;
                Vector3 axis = Vector3.Cross(dir, palm);
                if (axis.sqrMagnitude < 1e-6f) break;
                // Turning about (dir x palm) swings the joint towards the palm.
                bone.rotation = Quaternion.AngleAxis(joint == 0 ? angles.x : joint == 1 ? angles.y : angles.z, axis.normalized) * bone.rotation;
                bone = child;
            }
        }

        // ---------------------------------------------------------------- helpers

        private void SetRiding(bool riding)
        {
            if (_mm != null && _mm.isActiveAndEnabled) { if (_mm.TryGetControllerLayer(out var l)) l.SetBool(RidingId, riding); }
            else if (_animator.runtimeAnimatorController != null) _animator.SetBool(RidingId, riding);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
