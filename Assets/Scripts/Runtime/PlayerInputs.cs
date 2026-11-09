using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Collects input from the new Input System via PlayerInput "Send Messages".
    /// Holds the current frame's intent so the controller and camera can read it.
    /// </summary>
    public class PlayerInputs : MonoBehaviour
    {
        [Header("Character Input Values")]
        public Vector2 move;
        public Vector2 look;
        public bool jump;
        public bool sprint;
        public bool crouch;
        [Tooltip("Set on press, cleared by whoever handles it (CharacterActions)")]
        public bool interact;
        public bool attack;
        [Tooltip("Held: face the camera and move sideways / backwards (read every frame from the Strafe action)")]
        public bool strafe;

        [Header("Mouse Cursor Settings")]
        public bool cursorLocked = true;
        public bool cursorInputForLook = true;

        [Tooltip("Control scheme whose look input is a stick rate rather than a mouse delta")]
        public string gamepadScheme = "Gamepad";

#if ENABLE_INPUT_SYSTEM
        private PlayerInput _playerInput;
        private InputAction _strafeAction;
        private bool _strafeLatched;
#endif

        /// <summary>True when look comes from a stick (a rate that must be scaled by deltaTime).</summary>
        public bool LookIsRate
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput != null && _playerInput.currentControlScheme == gamepadScheme;
#else
                return false;
#endif
            }
        }

#if ENABLE_INPUT_SYSTEM
        private void Awake()
        {
            _playerInput = GetComponent<PlayerInput>();
            if (_playerInput == null) _playerInput = GetComponentInParent<PlayerInput>();
            _strafeAction = _playerInput != null && _playerInput.actions != null
                ? _playerInput.actions.FindAction("Strafe")
                : null;
        }

        private void Update()
        {
            // Mouse: hold (polled, so it never misses a release). Gamepad: a stick click toggles it, like a lock-on.
            // Right mouse on a free cursor is not a strafe.
            if (_strafeAction != null)
            {
                bool fromPad = _strafeAction.activeControl != null && _strafeAction.activeControl.device is Gamepad;
                if (fromPad && _strafeAction.WasPressedThisFrame()) _strafeLatched = !_strafeLatched;
                if (!LookIsRate) _strafeLatched = false;
                bool held = !fromPad && _strafeAction.IsPressed() && cursorLocked;
                strafe = held || _strafeLatched;
            }

            // Escape releases the cursor, clicking back into the game locks it again.
            if (cursorLocked && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                cursorLocked = false;
                SetCursorState(false);
            }
            else if (!cursorLocked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame &&
                     !IsPointerOverUI())
            {
                cursorLocked = true;
                SetCursorState(true);
            }
        }

        // Clicking a HUD button must not re-lock the cursor.
        private static bool IsPointerOverUI() =>
            UnityEngine.EventSystems.EventSystem.current != null &&
            UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();

        public void OnMove(InputValue value) => MoveInput(value.Get<Vector2>());
        public void OnLook(InputValue value)
        {
            Vector2 v = value.Get<Vector2>();
            // Ignore mouse look while the cursor is free so moving it to other windows does not spin the camera.
            if (!LookIsRate && (!cursorInputForLook || !cursorLocked)) v = Vector2.zero;
            LookInput(v);
        }
        public void OnJump(InputValue value) => JumpInput(value.isPressed);
        public void OnSprint(InputValue value) => SprintInput(value.isPressed);
        public void OnCrouch(InputValue value)
        {
            // Toggle crouch on press.
            if (value.isPressed) crouch = !crouch;
        }
        public void OnInteract(InputValue value) { if (value.isPressed) interact = true; }
        public void OnAttack(InputValue value)
        {
            // The click that re-locks a free cursor is not an attack.
            if (value.isPressed && (cursorLocked || LookIsRate)) attack = true;
        }
#endif

        public void MoveInput(Vector2 newMoveDirection) => move = newMoveDirection;
        public void LookInput(Vector2 newLookDirection) => look = newLookDirection;
        public void JumpInput(bool newJumpState) => jump = newJumpState;
        public void SprintInput(bool newSprintState) => sprint = newSprintState;

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) SetCursorState(cursorLocked);
        }

        private void SetCursorState(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
