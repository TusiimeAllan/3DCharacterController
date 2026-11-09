using UnityEngine;

namespace Transport
{
    /// <summary>
    /// Rig driver for a rideable two-wheeler. Children are bound by name (as exported by the
    /// Transport pipeline): *_Steer, *_Wheel_Front, *_Wheel_Rear, *_Crank, *_Pedal_L/R and
    /// Socket_* empties. Every control is a plain field, so Animator / Timeline can keyframe it,
    /// or tick <see cref="autoFromMotion"/> to derive wheel spin and pedalling from movement.
    /// Steer pivots are pre-tilted by the rake angle: steering is a local Y rotation.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public class RideableVehicle : MonoBehaviour
    {
        public enum Kind { Bicycle, Motorbike, Scooter }

        [Header("Controls (animatable)")]
        [Range(-45, 45)] public float steerAngle;
        [Tooltip("Accumulated wheel rotation in degrees (forward is positive).")]
        public float wheelRotation;
        [Tooltip("Accumulated crank rotation in degrees. Ignored when the vehicle has no crank.")]
        public float crankRotation;
        [Tooltip("Derive wheelRotation / crankRotation from how far the root moved each frame.")]
        public bool autoFromMotion = true;

        [Header("Spec (filled by the importer)")]
        public Kind kind;
        public float frontWheelRadius = 0.3f;
        public float rearWheelRadius = 0.3f;
        [Tooltip("Crank turns per rear-wheel turn (1 / gear ratio).")]
        public float crankPerWheelTurn = 0.5f;
        public float maxSteerAngle = 35f;
        public float rakeDegrees = 22f;
        public float seatHeight;

        [Header("Rig (bound by name)")]
        public Transform steer, wheelFront, wheelRear, crank, pedalL, pedalR;
        public Transform seat, handL, handR, footL, footR, mountL, mountR;

        Quaternion steerRest, wfRest, wrRest, crankRest, plRest, prRest;
        Vector3 lastPos;
        bool cached;

        void OnEnable() { Bind(); CacheRest(); lastPos = transform.position; }

        public void Bind()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (n.EndsWith("_Steer")) steer = t;
                else if (n.EndsWith("_Wheel_Front")) wheelFront = t;
                else if (n.EndsWith("_Wheel_Rear")) wheelRear = t;
                else if (n.EndsWith("_Crank")) crank = t;
                else if (n.EndsWith("_Pedal_L")) pedalL = t;
                else if (n.EndsWith("_Pedal_R")) pedalR = t;
                else if (n == "Socket_Seat") seat = t;
                else if (n == "Socket_Hand_L") handL = t;
                else if (n == "Socket_Hand_R") handR = t;
                else if (n == "Socket_Foot_L") footL = t;
                else if (n == "Socket_Foot_R") footR = t;
                else if (n == "Socket_Mount_L") mountL = t;
                else if (n == "Socket_Mount_R") mountR = t;
            }
        }

        void CacheRest()
        {
            if (steer) steerRest = steer.localRotation;
            if (wheelFront) wfRest = wheelFront.localRotation;
            if (wheelRear) wrRest = wheelRear.localRotation;
            if (crank) crankRest = crank.localRotation;
            if (pedalL) plRest = pedalL.localRotation;
            if (pedalR) prRest = pedalR.localRotation;
            cached = true;
        }

        void LateUpdate()
        {
            if (!cached) CacheRest();
            if (autoFromMotion && Application.isPlaying)
            {
                Vector3 d = transform.position - lastPos;
                float dist = Vector3.Dot(d, transform.forward);
                wheelRotation += dist / (2f * Mathf.PI * rearWheelRadius) * 360f;
                if (crank) crankRotation += dist / (2f * Mathf.PI * rearWheelRadius) * 360f * crankPerWheelTurn;
            }
            lastPos = transform.position;
            Apply();
        }

        /// <summary>Pose the rig from the current control values.</summary>
        public void Apply()
        {
            float s = Mathf.Clamp(steerAngle, -maxSteerAngle, maxSteerAngle);
            if (steer) steer.localRotation = steerRest * Quaternion.AngleAxis(s, Vector3.up);
            float frontSpin = wheelRotation * rearWheelRadius / Mathf.Max(0.01f, frontWheelRadius);
            if (wheelFront) wheelFront.localRotation = wfRest * Quaternion.AngleAxis(frontSpin, Vector3.right);
            if (wheelRear) wheelRear.localRotation = wrRest * Quaternion.AngleAxis(wheelRotation, Vector3.right);
            if (crank)
            {
                crank.localRotation = crankRest * Quaternion.AngleAxis(crankRotation, Vector3.right);
                // pedals stay level: cancel the crank rotation
                if (pedalL) pedalL.localRotation = plRest * Quaternion.AngleAxis(-crankRotation, Vector3.right);
                if (pedalR) pedalR.localRotation = prRest * Quaternion.AngleAxis(-crankRotation, Vector3.right);
            }
        }

        void OnDrawGizmosSelected()
        {
            void Dot(Transform t, Color c, float r) { if (!t) return; Gizmos.color = c; Gizmos.DrawSphere(t.position, r); }
            Dot(seat, Color.yellow, 0.04f);
            Dot(handL, Color.magenta, 0.03f); Dot(handR, Color.magenta, 0.03f);
            Dot(footL, Color.cyan, 0.03f); Dot(footR, Color.cyan, 0.03f);
            Dot(mountL, Color.green, 0.05f); Dot(mountR, Color.green, 0.05f);
            if (steer) { Gizmos.color = Color.blue; Gizmos.DrawLine(steer.position - steer.up * 0.6f, steer.position + steer.up * 0.2f); }
        }
    }
}
