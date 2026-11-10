using UnityEngine;
using UnityEngine.Events;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Something the character can get on: a bike, a car seat, a horse, a chair. Put it on the vehicle (or
    /// the seat) and position the transforms; the character's <see cref="CharacterMountController"/> does the
    /// rest. While mounted, the player's input is handed to <see cref="DriveInput"/>, so a vehicle script can
    /// subclass this (or listen to <see cref="Input"/>) to drive itself.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterMount : MonoBehaviour
    {
        [Tooltip("Where the hips sit (its forward is the way the rider faces). Defaults to this transform")]
        public Transform Seat;
        [Tooltip("Where the rider stands after getting off. Empty = beside the seat")]
        public Transform ExitPoint;
        [Tooltip("Distance from which the rider can get on")]
        public float MountRange = 1.6f;
        [Tooltip("Animator MountPose value while on this mount (0 = sit, 1 = ride, 2 = drive, or your own)")]
        public int Pose;

        [Header("Limb Targets (optional)")]
        public Transform LeftHand;
        public Transform RightHand;
        public Transform LeftFoot;
        public Transform RightFoot;
        [Tooltip("Optional point the knees bend towards")]
        public Transform LeftKneeHint;
        public Transform RightKneeHint;
        [Tooltip("Where the rider looks. Empty = straight ahead")]
        public Transform LookTarget;

        [Header("Events")]
        public UnityEvent<CharacterMountController> Mounted = new UnityEvent<CharacterMountController>();
        public UnityEvent<CharacterMountController> Dismounted = new UnityEvent<CharacterMountController>();
        [Tooltip("Rider input every frame while mounted: move (x steer, y throttle), sprint, jump")]
        public UnityEvent<Vector2, bool, bool> Input = new UnityEvent<Vector2, bool, bool>();

        public CharacterMountController Rider { get; private set; }
        public Transform SeatTransform => Seat != null ? Seat : transform;

        public virtual bool CanMount(CharacterMountController rider) => Rider == null && isActiveAndEnabled;

        /// <summary>Called by the rider; override to start an engine, hand over a camera, etc.</summary>
        public virtual void OnMounted(CharacterMountController rider)
        {
            Rider = rider;
            Mounted.Invoke(rider);
        }

        public virtual void OnDismounted(CharacterMountController rider)
        {
            Rider = null;
            Dismounted.Invoke(rider);
        }

        /// <summary>The rider's input while mounted. Override in a vehicle to drive it.</summary>
        public virtual void DriveInput(Vector2 move, bool sprint, bool jump) => Input.Invoke(move, sprint, jump);

        /// <summary>Where the rider is placed on getting off.</summary>
        public virtual Vector3 ExitPosition(Transform rider)
        {
            if (ExitPoint != null) return ExitPoint.position;
            var seat = SeatTransform;
            return seat.position - seat.right * 0.9f + Vector3.down * 0.4f;
        }

        protected virtual void OnDisable()
        {
            if (Rider != null) Rider.Dismount();
        }

        protected virtual void OnDrawGizmosSelected()
        {
            var seat = SeatTransform;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireSphere(seat.position, 0.12f);
            Gizmos.DrawLine(seat.position, seat.position + seat.forward * 0.4f);
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.15f);
            Gizmos.DrawWireSphere(seat.position, MountRange);
            foreach (var t in new[] { LeftHand, RightHand, LeftFoot, RightFoot })
                if (t != null) Gizmos.DrawWireCube(t.position, Vector3.one * 0.06f);
        }
    }
}
