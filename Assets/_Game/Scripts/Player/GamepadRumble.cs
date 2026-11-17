using ForestSoldier.Combat;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Short rumble on each shot and a heavier one on a kill, only while the
    /// player is using a gamepad. Motors are stopped on pause, focus loss and disable.
    /// </summary>
    public class GamepadRumble : MonoBehaviour
    {
        [SerializeField] private SoldierInput input;
        [SerializeField] private HitscanWeapon weapon;
        [SerializeField] private Vector2 shot = new Vector2(0.12f, 0.32f);
        [SerializeField] private float shotTime = 0.06f;
        [SerializeField] private Vector2 kill = new Vector2(0.45f, 0.2f);
        [SerializeField] private float killTime = 0.18f;
        [Range(0f, 1f)] [SerializeField] private float strength = 1f;

        private float until;
        private bool running;

        private void Awake()
        {
            if (input == null) input = GetComponent<SoldierInput>();
            if (weapon == null) weapon = GetComponent<HitscanWeapon>();
        }

        private void OnEnable()
        {
            if (weapon == null) return;
            weapon.Fired += OnFired;
            weapon.HitDamageable += OnHit;
        }

        private void OnDisable()
        {
            if (weapon != null)
            {
                weapon.Fired -= OnFired;
                weapon.HitDamageable -= OnHit;
            }
            Stop();
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus) Stop();
        }

        private void OnFired(ShotResult _) => Pulse(shot, shotTime);

        private void OnHit(ShotResult _, IDamageable __, DamageResult result)
        {
            if (result.Killed) Pulse(kill, killTime);
        }

        private void Pulse(Vector2 motors, float seconds)
        {
            var pad = Gamepad.current;
            if (pad == null || input == null || !input.UsingGamepad || !input.CursorLocked || strength <= 0f) return;
            pad.SetMotorSpeeds(motors.x * strength, motors.y * strength);
            until = Mathf.Max(until, Time.unscaledTime + seconds);
            running = true;
        }

        private void Update()
        {
            if (!running) return;
            if (Time.unscaledTime >= until || input == null || !input.CursorLocked) Stop();
        }

        private void Stop()
        {
            if (!running) return;
            running = false;
            Gamepad.current?.SetMotorSpeeds(0f, 0f);
        }
    }
}
