using UnityEngine;

namespace Transport
{
    /// <summary>
    /// Seats a Humanoid rider on a <see cref="RideableVehicle"/>: hips on Socket_Seat, hands on the
    /// grips (which follow steering), feet on the pedals / pegs. Put it on the rider's Animator
    /// object and tick "IK Pass" on the base layer of the Animator Controller. Play any sitting /
    /// riding clip underneath; this only pins the end effectors.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class RiderIK : MonoBehaviour
    {
        public RideableVehicle vehicle;
        [Range(0, 1)] public float weight = 1f;
        [Tooltip("Hips sit this far above the seat socket.")]
        public float hipOffset = 0.08f;
        [Tooltip("Move the rider root so the hips land on the seat every frame.")]
        public bool snapToSeat = true;
        [Tooltip("Knees bend forward and slightly out.")]
        public Vector3 kneeHint = new Vector3(0.15f, 0.4f, 0.5f);

        Animator anim;

        void Awake() => anim = GetComponent<Animator>();

        void LateUpdate()
        {
            if (!snapToSeat || !vehicle || !vehicle.seat || !anim || !anim.isHuman) return;
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            if (!hips) return;
            Vector3 target = vehicle.seat.position + vehicle.seat.up * hipOffset;
            transform.position += target - hips.position;
            transform.rotation = Quaternion.LookRotation(vehicle.transform.forward, vehicle.transform.up);
        }

        void OnAnimatorIK(int layer)
        {
            if (!vehicle || !anim) return;
            Pin(AvatarIKGoal.LeftHand, vehicle.handL);
            Pin(AvatarIKGoal.RightHand, vehicle.handR);
            Pin(AvatarIKGoal.LeftFoot, vehicle.footL);
            Pin(AvatarIKGoal.RightFoot, vehicle.footR);
            var v = vehicle.transform;
            anim.SetIKHintPositionWeight(AvatarIKHint.LeftKnee, weight);
            anim.SetIKHintPositionWeight(AvatarIKHint.RightKnee, weight);
            // hints are relative to the vehicle root (Unity left is -X)
            anim.SetIKHintPosition(AvatarIKHint.LeftKnee, v.TransformPoint(new Vector3(-kneeHint.x, kneeHint.y, kneeHint.z)));
            anim.SetIKHintPosition(AvatarIKHint.RightKnee, v.TransformPoint(kneeHint));
        }

        void Pin(AvatarIKGoal goal, Transform t)
        {
            if (!t) { anim.SetIKPositionWeight(goal, 0); anim.SetIKRotationWeight(goal, 0); return; }
            anim.SetIKPositionWeight(goal, weight);
            anim.SetIKPosition(goal, t.position);
            // feet flat along the vehicle, hands wrap the grip
            bool foot = goal == AvatarIKGoal.LeftFoot || goal == AvatarIKGoal.RightFoot;
            var fwd = vehicle.transform.forward;
            anim.SetIKRotationWeight(goal, foot ? weight : weight * 0.5f);
            anim.SetIKRotation(goal, Quaternion.LookRotation(fwd, vehicle.transform.up));
        }
    }
}
