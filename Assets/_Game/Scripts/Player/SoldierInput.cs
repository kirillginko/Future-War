using UnityEngine;
using UnityEngine.InputSystem;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Reads the Player action map once per frame and exposes plain values.
    /// Other components read from here instead of touching the Input System,
    /// so tests (and later, AI or network input) can drive the soldier by
    /// setting <see cref="ExternalControl"/> and writing the properties.
    ///
    /// Keyboard/mouse and an Xbox-style gamepad work side by side. On the
    /// gamepad the left stick's deflection sets the pace (a light push walks,
    /// full tilt runs), B toggles crouch and Menu toggles pause. <see cref="UsingGamepad"/> follows the last device used.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class SoldierInput : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [Tooltip("Lock and hide the cursor when play starts.")]
        [SerializeField] private bool lockCursorOnStart = true;

        public bool ExternalControl { get; set; }

        public Vector2 Move { get; set; }
        /// <summary>Move came from an analog stick: its length sets the pace (walk → run) instead of Sprint.</summary>
        public bool MoveIsAnalog { get; set; }
        /// <summary>Mouse look, in pixels this frame.</summary>
        public Vector2 Look { get; set; }
        /// <summary>Right stick, -1..1 (deadzone applied); the camera turns it into degrees per second.</summary>
        public Vector2 LookStick { get; set; }
        public bool Sprint { get; set; }
        public bool Aim { get; set; }
        public bool Crouch { get; set; }
        /// <summary>Fire button held (false while the click that captured the cursor is still down).</summary>
        public bool Fire { get; set; }
        /// <summary>Set on the press; the consumer clears it.</summary>
        public bool ReloadRequested { get; set; }
        /// <summary>Set on the press; the consumer clears it.</summary>
        public bool ResetTargetsRequested { get; set; }
        public bool FrontViewRequested { get; set; }
        public bool FirstPersonRequested { get; set; }
        public bool CursorLocked { get; private set; }
        /// <summary>The last input came from a gamepad (for prompts, rumble and aim slowdown).</summary>
        public bool UsingGamepad { get; set; }

        private InputActionMap map;
        private InputAction move, look, lookStick, sprint, aim, crouch, crouchToggle, fire, reload, resetTargets, pause;
        private InputAction frontView, firstPersonView;
        private bool fireHeldSinceCapture;
        private bool crouchLatched;

        private void Awake()
        {
            if (actions == null)
            {
                Debug.LogError($"{name}: SoldierInput has no InputActionAsset assigned.", this);
                enabled = false;
                return;
            }
            map = actions.FindActionMap("Player", throwIfNotFound: true);
            move = map.FindAction("Move", true);
            look = map.FindAction("Look", true);
            lookStick = map.FindAction("LookStick", true);
            sprint = map.FindAction("Sprint", true);
            aim = map.FindAction("Aim", true);
            crouch = map.FindAction("Crouch", true);
            crouchToggle = map.FindAction("CrouchToggle", true);
            fire = map.FindAction("Fire", true);
            reload = map.FindAction("Reload", true);
            resetTargets = map.FindAction("ResetTargets", true);
            pause = map.FindAction("Pause", true);
            frontView = map.FindAction("FrontView", true);
            firstPersonView = map.FindAction("FirstPersonView", true);
        }

        private void OnEnable()
        {
            if (map == null) return;
            map.Enable();
            map.actionTriggered += OnActionTriggered;
        }

        private void OnDisable()
        {
            if (map == null) return;
            map.actionTriggered -= OnActionTriggered;
            map.Disable();
        }

        private void OnActionTriggered(InputAction.CallbackContext ctx)
        {
            if (ExternalControl || ctx.control == null) return;
            UsingGamepad = ctx.control.device is Gamepad;
        }

        private void Start()
        {
            if (lockCursorOnStart && !Application.isBatchMode) SetCursorLocked(true);
        }

        private void Update()
        {
            if (ExternalControl) return;

            if (pause.WasPressedThisFrame())
            {
                // Escape frees the cursor; the gamepad's Menu button toggles.
                bool fromPad = pause.activeControl != null && pause.activeControl.device is Gamepad;
                SetCursorLocked(fromPad && !CursorLocked);
            }
            else if (!CursorLocked && fire.WasPressedThisFrame())
            {
                SetCursorLocked(true);
                fireHeldSinceCapture = true; // the capturing click is not a shot
            }
            if (fireHeldSinceCapture && !fire.IsPressed()) fireHeldSinceCapture = false;

            Move = CursorLocked ? move.ReadValue<Vector2>() : Vector2.zero;
            Look = CursorLocked ? look.ReadValue<Vector2>() : Vector2.zero;
            LookStick = CursorLocked ? lookStick.ReadValue<Vector2>() : Vector2.zero;
            Aim = CursorLocked && aim.IsPressed();

            if (Move.sqrMagnitude > 1e-4f)
                MoveIsAnalog = move.activeControl != null && move.activeControl.device is Gamepad;

            // B toggles crouch; sprinting (Shift) stands you up.
            if (crouchToggle.WasPressedThisFrame()) crouchLatched = !crouchLatched;
            Sprint = sprint.IsPressed();
            if (Sprint) crouchLatched = false;
            Crouch = CursorLocked && (crouch.IsPressed() || crouchLatched);
            Fire = CursorLocked && !fireHeldSinceCapture && fire.IsPressed();
            if (CursorLocked && reload.WasPressedThisFrame()) ReloadRequested = true;
            if (resetTargets.WasPressedThisFrame()) ResetTargetsRequested = true;
            if (CursorLocked && frontView.WasPressedThisFrame()) FrontViewRequested = true;
            if (CursorLocked && firstPersonView.WasPressedThisFrame()) FirstPersonRequested = true;
        }

        public void SetCursorLocked(bool locked)
        {
            CursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void OnApplicationFocus(bool focus)
        {
            // Regaining focus should not silently re-lock; the player clicks to resume.
            if (!focus && CursorLocked) SetCursorLocked(false);
        }
    }
}
