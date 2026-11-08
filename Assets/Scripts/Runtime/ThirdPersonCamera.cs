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
        [Tooltip("Extra look height above the target pivot")]
        public float HeightOffset = 0f;
        public float TopClamp = 70.0f;
        public float BottomClamp = -30.0f;

        [Header("Collision")]
        public LayerMask CollisionLayers = ~0;
        public float CollisionRadius = 0.2f;

        private float _yaw;
        private float _pitch = 15f;

        private void Start()
        {
            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;
        }

        private void LateUpdate()
        {
            if (Target == null) return;

            if (Input != null && Input.look.sqrMagnitude > 0.0001f)
            {
                _yaw += Input.look.x * MouseSensitivity;
                _pitch -= Input.look.y * MouseSensitivity;
            }
            _pitch = Mathf.Clamp(_pitch, BottomClamp, TopClamp);

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = Target.position + Vector3.up * HeightOffset;
            Vector3 desired = pivot - rotation * Vector3.forward * Distance;

            // Pull in if something is between the pivot and the camera.
            if (Physics.SphereCast(pivot, CollisionRadius, (desired - pivot).normalized,
                    out RaycastHit hit, Distance, CollisionLayers, QueryTriggerInteraction.Ignore))
            {
                desired = pivot - rotation * Vector3.forward * (hit.distance - CollisionRadius);
            }

            transform.position = desired;
            transform.rotation = rotation;
        }
    }
}
