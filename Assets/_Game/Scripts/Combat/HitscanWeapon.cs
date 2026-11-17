using System;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// Automatic hitscan rifle. Aims with the camera (screen centre), fires
    /// from the muzzle (so cover in front of the gun stops the round), with
    /// magazine, reload, spread and recoil. Presentation hooks are events plus
    /// optional references (effects, aim rig, reload overlay).
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class HitscanWeapon : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SoldierInput input;
        [SerializeField] private SoldierMotor motor;
        [SerializeField] private ThirdPersonCamera cameraRig;
        [Tooltip("Barrel tip; +Z along the barrel.")]
        [SerializeField] private Transform muzzle;
        [Tooltip("Point on the player's capsule axis at shoulder height.")]
        [SerializeField] private Transform shooterOrigin;
        [SerializeField] private SoldierAimRig aimRig;
        [SerializeField] private UpperBodyOverlay overlay;
        [SerializeField] private WeaponEffects effects;

        [Header("Stats")]
        [SerializeField] private float roundsPerMinute = 600f;
        [SerializeField] private float damage = 25f;
        [SerializeField] private float range = 200f;
        [SerializeField] private int magazineSize = 30;
        [SerializeField] private float reloadTime = 2.2f;
        [Tooltip("Ammo is in the gun at this fraction of the reload; cancelling earlier keeps the old count.")]
        [Range(0.3f, 1f)] [SerializeField] private float reloadCommitFraction = 0.8f;
        [SerializeField] private LayerMask hitMask = ~0;

        [Header("Accuracy (cone half-angle, degrees)")]
        [SerializeField] private float aimSpread = 0.2f;
        [SerializeField] private float hipSpread = 1.4f;
        [SerializeField] private float movingSpreadAdd = 0.8f;
        [SerializeField] private float spreadPerShot = 0.25f;
        [SerializeField] private float maxShotSpread = 1.5f;
        [SerializeField] private float spreadRecovery = 4f;
        [Tooltip("The body must be turned this close to the camera's yaw before the first shot.")]
        [SerializeField] private float maxFacingErrorToFire = 25f;

        [Header("Recoil")]
        [SerializeField] private float gunKickPitch = 2.2f;
        [SerializeField] private float gunKickYaw = 0.6f;
        [SerializeField] private float cameraKickPitch = 0.3f;
        [SerializeField] private float cameraKickYaw = 0.08f;

        public int Ammo { get; private set; }
        public int MagazineSize => magazineSize;
        public float Range => range;
        public float Damage => damage;
        public bool IsReloading { get; private set; }
        public float ReloadProgress => IsReloading ? Mathf.Clamp01(1f - (reloadEnd - Time.time) / reloadTime) : 0f;
        /// <summary>Current cone half-angle in degrees (for the crosshair).</summary>
        public float CurrentSpread { get; private set; }
        /// <summary>What the crosshair is over this frame.</summary>
        public Vector3 AimPoint { get; private set; }
        public Transform Muzzle => muzzle;
        public int ShotsFired { get; private set; }
        public bool TriggerHeld => input != null && input.Fire;

        public event Action<ShotResult> Fired;
        public event Action<ShotResult, IDamageable, DamageResult> HitDamageable;
        public event Action ReloadStarted;
        public event Action Reloaded;
        public event Action DryFired;

        private float nextShotTime;
        private float reloadEnd;
        private float shotBloom;
        private bool triggerWasDown;
        private bool reloadCommitted;

        private void Awake()
        {
            Ammo = magazineSize;
            if (input == null) input = GetComponent<SoldierInput>();
            if (motor == null) motor = GetComponent<SoldierMotor>();
            if (cameraRig == null) cameraRig = FindFirstObjectByType<ThirdPersonCamera>();
        }

        public void SetCamera(ThirdPersonCamera rig) => cameraRig = rig;

        private Vector3 ShooterPoint =>
            (shooterOrigin != null ? shooterOrigin.position : transform.position + Vector3.up * 1.45f)
            - Vector3.up * (motor != null ? motor.CrouchDrop : 0f);

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (cameraRig == null || muzzle == null) return;

            AimPoint = HitscanResolver.AimPoint(cameraRig.AimRay, ShooterPoint, range, hitMask, out _, out _);
            if (aimRig != null && aimRig.isActiveAndEnabled) aimRig.ApplyAim();

            shotBloom = Mathf.MoveTowards(shotBloom, 0f, spreadRecovery * dt);
            bool moving = motor != null && motor.AnimationVelocity.sqrMagnitude > 0.25f;
            float baseSpread = (input.Aim ? aimSpread : hipSpread) + (moving ? movingSpreadAdd * (input.Aim ? 0.4f : 1f) : 0f);
            CurrentSpread = baseSpread + shotBloom;

            if (IsReloading)
            {
                if (!reloadCommitted && Time.time >= reloadEnd - reloadTime * (1f - reloadCommitFraction))
                {
                    reloadCommitted = true;
                    Ammo = magazineSize;
                }
                if (Time.time >= reloadEnd)
                {
                    IsReloading = false;
                    Reloaded?.Invoke();
                }
            }

            if (input.ReloadRequested)
            {
                input.ReloadRequested = false;
                TryReload();
            }

            bool trigger = input.Fire;
            bool pressed = trigger && !triggerWasDown;
            triggerWasDown = trigger;
            if (!trigger || IsReloading) return;

            if (Ammo <= 0)
            {
                if (pressed)
                {
                    DryFired?.Invoke();
                    if (effects != null) effects.PlayDryFire();
                    TryReload();
                }
                return;
            }

            if (motor != null && (!motor.FacingCamera || motor.FacingError > maxFacingErrorToFire)) return;

            float interval = 60f / Mathf.Max(1f, roundsPerMinute);
            if (nextShotTime < Time.time - interval) nextShotTime = Time.time;
            while (!IsReloading && Time.time >= nextShotTime && Ammo > 0)
            {
                FireOnce();
                nextShotTime += interval;
            }
            if (Ammo == 0) TryReload();
        }

        public bool TryReload()
        {
            if (IsReloading || Ammo >= magazineSize) return false;
            IsReloading = true;
            reloadCommitted = false;
            reloadEnd = Time.time + reloadTime;
            if (overlay != null) overlay.PlayReload(reloadTime);
            if (effects != null) effects.PlayReload(reloadTime);
            ReloadStarted?.Invoke();
            return true;
        }

        private void FireOnce()
        {
            Ammo--;
            ShotsFired++;

            Ray ray = cameraRig.AimRay;
            ray.direction = Spread(ray.direction, CurrentSpread);
            ShotResult shot = HitscanResolver.Resolve(ray, muzzle.position, ShooterPoint, range, hitMask);

            IDamageable target = null;
            DamageResult result = default;
            if (shot.Hit)
            {
                target = shot.Info.collider.GetComponentInParent<IDamageable>();
                if (target != null && target.IsAlive)
                {
                    Vector3 dir = (shot.End - muzzle.position).normalized;
                    result = target.ApplyDamage(new DamageInfo(damage, shot.Info.point, shot.Info.normal, dir, this));
                }
            }

            shotBloom = Mathf.Min(maxShotSpread, shotBloom + spreadPerShot);
            float yawKick = UnityEngine.Random.Range(-1f, 1f);
            if (aimRig != null) aimRig.AddRecoil(gunKickPitch, gunKickYaw * yawKick);
            cameraRig.AddRecoil(cameraKickPitch, cameraKickYaw * yawKick);
            if (effects != null) effects.PlayShot(muzzle, shot, result.Landed);

            Fired?.Invoke(shot);
            if (result.Landed) HitDamageable?.Invoke(shot, target, result);
        }

        private static Vector3 Spread(Vector3 dir, float halfAngleDeg)
        {
            if (halfAngleDeg <= 0f) return dir;
            // uniform over the disc of the cone's base
            Vector2 d = UnityEngine.Random.insideUnitCircle * Mathf.Tan(halfAngleDeg * Mathf.Deg2Rad);
            Quaternion basis = Quaternion.LookRotation(dir);
            return (basis * new Vector3(d.x, d.y, 1f)).normalized;
        }
    }
}
