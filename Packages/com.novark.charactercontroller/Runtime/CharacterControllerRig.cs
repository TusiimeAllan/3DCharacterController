using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// The one component to tune the player from. Its Inspector gathers every part of the rig (movement,
    /// camera, body, parkour, climbing, swimming, actions, IK, motion matching, mounts and sockets) into one
    /// place; the parts themselves are hidden from the Inspector. Game code can reach every part from here:
    /// <code>var rig = player.GetComponent&lt;CharacterControllerRig&gt;(); rig.Movement.SprintSpeed = 7f;</code>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Character Controller/Character Controller")]
    public class CharacterControllerRig : MonoBehaviour
    {
        [Tooltip("Show the individual components in the Inspector too (for debugging)")]
        public bool ShowComponents;

        public ThirdPersonController Movement => Get(ref _movement);
        public PlayerInputs Inputs => Get(ref _inputs);
        public CharacterDimensions Body => Get(ref _body);
        public CharacterSwitcher Characters => Get(ref _characters);
        public ParkourController Parkour => Get(ref _parkour);
        public ClimbController Climbing => Get(ref _climbing);
        public SwimController Swimming => Get(ref _swimming);
        public CharacterActions Actions => Get(ref _actions);
        public HumanoidGrounding Grounding => Get(ref _grounding);
        public BodyContactSolver BodyContact => Get(ref _bodyContact);
        public LocomotionAnimationHost LocomotionHost => Get(ref _host);
        public CharacterMountController Mounts => Get(ref _mounts);
        public CharacterSockets Sockets => Get(ref _sockets);
        public Animator Animator => Get(ref _animator);
        public CharacterController Capsule => Get(ref _capsule);

        /// <summary>The orbit camera following this character, if any.</summary>
        public ThirdPersonCamera Camera
        {
            get
            {
                if (_camera != null) return _camera;
                foreach (var cam in FindObjectsByType<ThirdPersonCamera>(FindObjectsInactive.Include))
                    if (cam.Target != null && cam.Target.IsChildOf(transform)) return _camera = cam;
                return null;
            }
        }

        private ThirdPersonController _movement;
        private PlayerInputs _inputs;
        private CharacterDimensions _body;
        private CharacterSwitcher _characters;
        private ParkourController _parkour;
        private ClimbController _climbing;
        private SwimController _swimming;
        private CharacterActions _actions;
        private HumanoidGrounding _grounding;
        private BodyContactSolver _bodyContact;
        private LocomotionAnimationHost _host;
        private CharacterMountController _mounts;
        private CharacterSockets _sockets;
        private Animator _animator;
        private CharacterController _capsule;
        private ThirdPersonCamera _camera;

        private T Get<T>(ref T cache) where T : Component
        {
            if (cache == null) cache = GetComponent<T>();
            return cache;
        }

        /// <summary>Stops every action in progress and puts the character at a point (respawn, teleport).</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (Mounts != null && Mounts.IsMounted) Mounts.Dismount();
            if (Parkour != null) Parkour.Cancel();
            bool ccOn = Capsule != null && Capsule.enabled;
            if (Capsule != null) Capsule.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();
            if (Capsule != null) Capsule.enabled = ccOn;
        }

        /// <summary>
        /// Hides (or shows) the rig's parts in the Inspector, so the rig is tuned from this component only. Only
        /// the kit's own components are hidden; your own scripts on the Player always stay visible.
        /// </summary>
        public static void HideParts(CharacterControllerRig rig, bool hide)
        {
            if (rig == null) return;
            foreach (var c in rig.GetComponents<Component>())
            {
                if (c == null || c is Transform || c == rig) continue;
                bool part = IsKitPart(c);
                var flags = c.hideFlags;
                c.hideFlags = hide && part ? flags | HideFlags.HideInInspector : flags & ~HideFlags.HideInInspector;
            }
        }

        /// <summary>A component the kit sets up and shows in this Inspector (not one added by your project).</summary>
        public static bool IsKitPart(Component c)
        {
            if (c is CharacterController || c is Animator) return true;
            var type = c.GetType();
            string assembly = type.Assembly.GetName().Name;
            return assembly.StartsWith("CharacterControllerSystem.") || assembly == "MxM.Runtime" ||
                   type.FullName == "UnityEngine.InputSystem.PlayerInput";
        }

        private void OnValidate() => HideParts(this, !ShowComponents);
    }
}
