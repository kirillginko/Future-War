using UnityEngine;
using UnityEngine.InputSystem;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Reads the Player action map once per frame and exposes plain values.
    /// Other components read from here instead of touching the Input System,
    /// so tests (and later, AI or network input) can drive the soldier by
    /// setting <see cref="ExternalControl"/> and writing the properties.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class SoldierInput : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [Tooltip("Lock and hide the cursor when play starts.")]
        [SerializeField] private bool lockCursorOnStart = true;

        public bool ExternalControl { get; set; }

        public Vector2 Move { get; set; }
        public Vector2 Look { get; set; }
        public bool Sprint { get; set; }
        public bool Aim { get; set; }
        /// <summary>Fire button held (false while the click that captured the cursor is still down).</summary>
        public bool Fire { get; set; }
        /// <summary>Set on the press; the consumer clears it.</summary>
        public bool ReloadRequested { get; set; }
        /// <summary>Set on the press; the consumer clears it.</summary>
        public bool ResetTargetsRequested { get; set; }
        public bool CursorLocked { get; private set; }

        private InputActionMap map;
        private InputAction move, look, sprint, aim, fire, reload, resetTargets, pause;
        private bool fireHeldSinceCapture;

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
            sprint = map.FindAction("Sprint", true);
            aim = map.FindAction("Aim", true);
            fire = map.FindAction("Fire", true);
            reload = map.FindAction("Reload", true);
            resetTargets = map.FindAction("ResetTargets", true);
            pause = map.FindAction("Pause", true);
        }

        private void OnEnable() => map?.Enable();
        private void OnDisable() => map?.Disable();

        private void Start()
        {
            if (lockCursorOnStart && !Application.isBatchMode) SetCursorLocked(true);
        }

        private void Update()
        {
            if (ExternalControl) return;

            if (pause.WasPressedThisFrame()) SetCursorLocked(false);
            else if (!CursorLocked && fire.WasPressedThisFrame())
            {
                SetCursorLocked(true);
                fireHeldSinceCapture = true; // the capturing click is not a shot
            }
            if (fireHeldSinceCapture && !fire.IsPressed()) fireHeldSinceCapture = false;

            Move = move.ReadValue<Vector2>();
            Sprint = sprint.IsPressed();
            Look = CursorLocked ? look.ReadValue<Vector2>() : Vector2.zero;
            Aim = CursorLocked && aim.IsPressed();
            Fire = CursorLocked && !fireHeldSinceCapture && fire.IsPressed();
            if (CursorLocked && reload.WasPressedThisFrame()) ReloadRequested = true;
            if (resetTargets.WasPressedThisFrame()) ResetTargetsRequested = true;
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
