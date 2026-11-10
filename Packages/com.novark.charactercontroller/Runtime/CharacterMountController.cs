using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Gets the character on and off <see cref="CharacterMount"/>s (bikes, cars, seats). While mounted the
    /// locomotion, parkour, climbing, swimming and grounding are paused, the hips sit on the seat, hands and
    /// feet reach for the mount's targets, and the move / sprint / jump input goes to the mount instead.
    /// Interact (E / gamepad west) next to a mount gets on; Interact again gets off.
    /// </summary>
    [DefaultExecutionOrder(-50)] // before CharacterActions, so Interact next to a mount is ours
    [DisallowMultipleComponent]
    public class CharacterMountController : MonoBehaviour
    {
        [Tooltip("Use Interact to get on the nearest mount in range and off again")]
        public bool InteractToggles = true;
        [Tooltip("Seconds to settle onto the seat")]
        public float MountBlend = 0.35f;
        [Range(0f, 1f)] public float HandWeight = 1f;
        [Range(0f, 1f)] public float FootWeight = 1f;

        public CharacterMount Current { get; private set; }
        public bool IsMounted => Current != null;
        public event System.Action<CharacterMount> MountChanged;

        private static readonly int MountedId = Animator.StringToHash("Mounted");
        private static readonly int MountPoseId = Animator.StringToHash("MountPose");
        private static readonly List<CharacterMount> Nearby = new List<CharacterMount>();

        private ThirdPersonController _controller;
        private PlayerInputs _inputs;
        private CharacterController _cc;
        private Animator _animator;
        private LocomotionAnimationHost _host;
        private readonly List<(Behaviour component, bool wasEnabled)> _paused = new List<(Behaviour, bool)>();
        private float _blend;
        private Vector3 _startPosition;
        private Quaternion _startRotation;

        private void Awake()
        {
            _controller = GetComponent<ThirdPersonController>();
            _inputs = GetComponent<PlayerInputs>();
            _cc = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _host = GetComponent<LocomotionAnimationHost>();
        }

        private void Update()
        {
            if (_inputs == null) return;
            if (IsMounted)
            {
                Current.DriveInput(_inputs.move, _inputs.sprint, _inputs.jump);
                _inputs.jump = false;
                if (InteractToggles && _inputs.interact)
                {
                    _inputs.interact = false;
                    Dismount();
                }
                return;
            }
            if (InteractToggles && _inputs.interact)
            {
                var mount = NearestMount();
                if (mount != null)
                {
                    _inputs.interact = false;
                    Mount(mount);
                }
            }
        }

        /// <summary>The closest mount in range that will take this rider, or null.</summary>
        public CharacterMount NearestMount()
        {
            CharacterMount best = null;
            float bestDistance = float.MaxValue;
            Nearby.Clear();
            Nearby.AddRange(FindObjectsByType<CharacterMount>(FindObjectsSortMode.None));
            float scale = TryGetComponent<CharacterDimensions>(out var d) ? d.Scale : 1f;
            foreach (var m in Nearby)
            {
                if (!m.CanMount(this)) continue;
                float distance = Vector3.Distance(transform.position, m.SeatTransform.position);
                if (distance > m.MountRange * Mathf.Max(1f, scale) || distance >= bestDistance) continue;
                best = m;
                bestDistance = distance;
            }
            return best;
        }

        public bool Mount(CharacterMount mount)
        {
            if (mount == null || IsMounted || !mount.CanMount(this)) return false;
            Current = mount;
            _startPosition = transform.position;
            _startRotation = transform.rotation;
            _blend = 0f;

            // Pause everything that moves or poses the body on its own.
            _paused.Clear();
            foreach (var b in GetComponents<Behaviour>())
            {
                if (!(b is ParkourController || b is ClimbController || b is SwimController ||
                      b is HumanoidGrounding || b is BodyContactSolver || b is CharacterActions)) continue;
                _paused.Add((b, b.enabled));
                b.enabled = false;
            }
            if (_controller != null) _controller.ExternallyDriven = true;
            if (_cc != null) _cc.enabled = false;
            SetAnimator(true, mount.Pose);
            mount.OnMounted(this);
            MountChanged?.Invoke(mount);
            return true;
        }

        public void Dismount()
        {
            if (!IsMounted) return;
            var mount = Current;
            Current = null;
            Vector3 exit = mount.ExitPosition(transform);
            Vector3 forward = mount.SeatTransform.forward;
            forward.y = 0f;
            transform.SetPositionAndRotation(exit, forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward) : transform.rotation);
            Physics.SyncTransforms();

            if (_cc != null) _cc.enabled = true;
            if (_controller != null) _controller.ExternallyDriven = false;
            foreach (var (component, wasEnabled) in _paused)
                if (component != null) component.enabled = wasEnabled;
            _paused.Clear();
            SetAnimator(false, 0);
            mount.OnDismounted(this);
            MountChanged?.Invoke(null);
        }

        private void LateUpdate()
        {
            if (!IsMounted) return;
            // Put the hips on the seat: move the whole character so the posed hips land there.
            var seat = Current.SeatTransform;
            _blend = MountBlend <= 0f ? 1f : Mathf.MoveTowards(_blend, 1f, Time.deltaTime / MountBlend);
            float k = Mathf.SmoothStep(0f, 1f, _blend);
            Vector3 forward = seat.forward;
            forward.y = 0f;
            Quaternion rotation = forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward, Vector3.up) : seat.rotation;
            transform.rotation = Quaternion.Slerp(_startRotation, rotation, k);
            var hips = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            Vector3 offset = hips != null ? hips.position - transform.position : Vector3.zero;
            transform.position = Vector3.Lerp(_startPosition, seat.position - offset, k);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (!IsMounted || _animator == null || !_animator.isHuman) return;
            var m = Current;
            float k = Mathf.SmoothStep(0f, 1f, _blend);
            Reach(AvatarIKGoal.LeftHand, m.LeftHand, HandWeight * k);
            Reach(AvatarIKGoal.RightHand, m.RightHand, HandWeight * k);
            Reach(AvatarIKGoal.LeftFoot, m.LeftFoot, FootWeight * k);
            Reach(AvatarIKGoal.RightFoot, m.RightFoot, FootWeight * k);
            Hint(AvatarIKHint.LeftKnee, m.LeftKneeHint, FootWeight * k);
            Hint(AvatarIKHint.RightKnee, m.RightKneeHint, FootWeight * k);
            if (m.LookTarget != null)
            {
                _animator.SetLookAtWeight(0.8f * k, 0.1f, 0.7f, 1f, 0.5f);
                _animator.SetLookAtPosition(m.LookTarget.position);
            }
        }

        private void Reach(AvatarIKGoal goal, Transform target, float weight)
        {
            if (target == null) { _animator.SetIKPositionWeight(goal, 0f); _animator.SetIKRotationWeight(goal, 0f); return; }
            _animator.SetIKPositionWeight(goal, weight);
            _animator.SetIKRotationWeight(goal, weight);
            _animator.SetIKPosition(goal, target.position);
            _animator.SetIKRotation(goal, target.rotation);
        }

        private void Hint(AvatarIKHint hint, Transform target, float weight)
        {
            if (target == null) { _animator.SetIKHintPositionWeight(hint, 0f); return; }
            _animator.SetIKHintPositionWeight(hint, weight);
            _animator.SetIKHintPosition(hint, target.position);
        }

        private void SetAnimator(bool mounted, int pose)
        {
            if (_host != null && _host.isActiveAndEnabled && _host.TryGetControllerLayer(out AnimatorControllerPlayable layer))
            {
                layer.SetBool(MountedId, mounted);
                layer.SetInteger(MountPoseId, pose);
            }
            else if (_animator != null && _animator.runtimeAnimatorController != null)
            {
                _animator.SetBool(MountedId, mounted);
                _animator.SetInteger(MountPoseId, pose);
            }
        }

        private void OnDisable() => Dismount();
    }
}
