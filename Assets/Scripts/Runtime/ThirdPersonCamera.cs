using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Simple orbit / follow camera driven by the same look input as the player.
    /// No Cinemachine dependency. Attach to the Main Camera.
    /// </summary>
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The point the camera looks at and orbits (usually a head-height socket on the player)")]
        public Transform Target;
        public PlayerInputs Input;

        [Header("Orbit")]
        public float Distance = 4.0f;
        public float MouseSensitivity = 1.0f;
        [Tooltip("Gamepad stick look multiplier, applied per second (stick input is a rate, not a delta)")]
        public float GamepadLookRate = 60.0f;
        [Tooltip("Extra look height above the target pivot")]
        public float HeightOffset = 0f;
        public float TopClamp = 70.0f;
        public float BottomClamp = -30.0f;

        [Header("Collision")]
        public LayerMask CollisionLayers = ~0;
        public float CollisionRadius = 0.2f;
        [Tooltip("Closest the camera may get to the pivot when pulled in by collision")]
        public float MinDistance = 0.5f;
        [Tooltip("How fast the camera eases back out after an obstruction clears")]
        public float RecoverSpeed = 6.0f;

        private float _yaw;
        private float _pitch = 15f;
        private float _currentDistance;

        private void Start()
        {
            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            // eulerAngles.x is 0..360, convert to -180..180 so the clamp works for upward pitch.
            _pitch = Mathf.DeltaAngle(0f, angles.x);
            _currentDistance = Distance;
        }

        private void LateUpdate()
        {
            if (Target == null) return;

            if (Input != null && Input.look.sqrMagnitude > 0.0001f)
            {
                // Mouse delta is already per-frame; stick input is a rate and must scale with time.
                float scale = Input.LookIsRate ? GamepadLookRate * Time.deltaTime : MouseSensitivity;
                _yaw += Input.look.x * scale;
                _pitch -= Input.look.y * scale;
            }
            _pitch = Mathf.Clamp(_pitch, BottomClamp, TopClamp);

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = Target.position + Vector3.up * HeightOffset;
            Vector3 back = -(rotation * Vector3.forward);

            // Pull in if something is between the pivot and the camera, never past MinDistance.
            float targetDistance = Distance;
            if (Physics.SphereCast(pivot, CollisionRadius, back, out RaycastHit hit, Distance,
                    CollisionLayers, QueryTriggerInteraction.Ignore))
            {
                targetDistance = Mathf.Clamp(hit.distance - CollisionRadius, MinDistance, Distance);
            }

            // Snap in instantly so we never clip through walls, ease back out smoothly.
            if (targetDistance < _currentDistance)
                _currentDistance = targetDistance;
            else
                _currentDistance = Mathf.Lerp(_currentDistance, targetDistance,
                    1f - Mathf.Exp(-RecoverSpeed * Time.deltaTime));

            transform.position = pivot + back * _currentDistance;
            transform.rotation = rotation;
        }
    }
}
