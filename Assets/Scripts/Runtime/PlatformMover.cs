using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Kinematic test platform: ping-pongs between its start position and an offset, and/or spins.
    /// Used in the test course to check whether the character is carried by moving ground.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PlatformMover : MonoBehaviour
    {
        [Tooltip("Travel from the start position, in local axes of the parent")]
        public Vector3 Offset = new Vector3(0f, 3f, 0f);
        [Tooltip("Seconds for one leg of the trip")]
        public float Duration = 3f;
        [Tooltip("Seconds to wait at each end")]
        public float Pause = 1f;
        [Tooltip("Spin speed in degrees per second around the up axis")]
        public float SpinSpeed;

        private Rigidbody _body;
        private Vector3 _start;
        private Quaternion _startRotation;
        private float _time;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _start = transform.position;
            _startRotation = transform.rotation;
        }

        private void FixedUpdate()
        {
            _time += Time.fixedDeltaTime;

            if (Offset != Vector3.zero && Duration > 0f)
            {
                float cycle = (Duration + Pause) * 2f;
                float t = Mathf.Repeat(_time, cycle);
                float k;
                if (t < Duration) k = t / Duration;
                else if (t < Duration + Pause) k = 1f;
                else if (t < Duration * 2f + Pause) k = 1f - (t - Duration - Pause) / Duration;
                else k = 0f;
                k = k * k * (3f - 2f * k); // ease in and out
                _body.MovePosition(_start + Offset * k);
            }

            if (!Mathf.Approximately(SpinSpeed, 0f))
                _body.MoveRotation(_startRotation * Quaternion.Euler(0f, SpinSpeed * _time, 0f));
        }
    }
}
