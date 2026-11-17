using System;
using System.Collections.Generic;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// The Bizon's under-barrel tube as a 40 mm grenade launcher. G / RB switches to it; fire launches
    /// a round along a ballistic arc solved so it lands on the crosshair's aim
    /// point (low arc if reachable, otherwise 45°). Rounds are simulated here,
    /// not as rigidbodies: a sphere cast per frame along the arc, so they're
    /// cheap and never tunnel. A round arms after a few metres; a hit before
    /// that is a dud. Single shot, reloads itself from the reserve.
    ///
    /// There's no separate launcher model: rounds leave from the front of the
    /// tube already under the Bizon's barrel (the model's "Mag" part), found
    /// from the weapon's Muzzle marker by <see cref="tubeOffset"/>. It's only
    /// available while a loadout weapon whose name contains <see cref="mountOn"/>
    /// is selected; switching weapons puts it away.
    /// </summary>
    [DefaultExecutionOrder(150)] // after input and the motor; before the rifle's LateUpdate
    public class GrenadeLauncher : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SoldierInput input;
        [SerializeField] private HitscanWeapon rifle;
        [SerializeField] private ThirdPersonCamera cameraRig;
        [SerializeField] private SoldierAimRig aimRig;
        [SerializeField] private CameraShake shake;
        [SerializeField] private GameObject roundPrefab;
        [Tooltip("Weapons (by loadout slot name) whose model has the launcher tube.")]
        [SerializeField] private string mountOn = "Bizon";
        [Tooltip("Front of the tube from the weapon's Muzzle marker, in metres (x right, y up, z along the barrel). " +
                 "Measured from the Bizon model: 6 cm behind and 3.7 cm below the muzzle.")]
        [SerializeField] private Vector3 tubeOffset = new Vector3(0f, -0.037f, -0.06f);
        [SerializeField] private ExplosionEffect explosionPrefab;

        [Header("Ballistics")]
        [SerializeField] private float muzzleSpeed = 55f;
        [SerializeField] private float gravity = 9.81f;
        [SerializeField] private float armingDistance = 6f;
        [SerializeField] private float maxFlightTime = 8f;
        [SerializeField] private float roundRadius = 0.035f;
        [SerializeField] private LayerMask hitMask = ~0;

        [Header("Blast")]
        [SerializeField] private float blastRadius = 7f;
        [SerializeField] private float blastDamage = 220f;
        [SerializeField] private float blastForce = 9f;

        [Header("Handling")]
        [SerializeField] private int reserve = 8;
        [SerializeField] private float reloadTime = 1.9f;
        [SerializeField] private float switchTime = 0.3f;
        [SerializeField] private float recoilPitch = 6f;
        [SerializeField] private float cameraKick = 1.6f;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip launchClip;
        [SerializeField] private AudioClip loadClip;

        public bool Active { get; private set; }
        public bool Loaded { get; private set; } = true;
        public int Reserve => reserve;
        public bool Reloading => reloadEnd > 0f;
        public int RoundsInFlight { get; private set; }
        public int MaxReserve { get; private set; }

        /// <summary>Raised when a round goes off (not for duds).</summary>
        public event Action<Vector3, ExplosionReport> Exploded;
        public event Action Launched;

        private class Round
        {
            public GameObject Visual;
            public TrailRenderer Trail;
            public Vector3 Pos, Vel;
            public float Age, Travelled;
            public bool Live;
        }

        private readonly List<Round> rounds = new List<Round>();
        private readonly List<ExplosionEffect> blasts = new List<ExplosionEffect>();
        private int nextBlast;
        private float reloadEnd, switchReady;
        private bool triggerWasDown;
        private int startReserve;
        private WeaponLoadout loadout;

        private void Awake()
        {
            if (input == null) input = GetComponent<SoldierInput>();
            if (rifle == null) rifle = GetComponent<HitscanWeapon>();
            if (aimRig == null) aimRig = GetComponent<SoldierAimRig>();
            if (cameraRig == null) cameraRig = FindFirstObjectByType<ThirdPersonCamera>();
            if (shake == null && cameraRig != null) shake = cameraRig.GetComponent<CameraShake>();
            startReserve = MaxReserve = reserve;

            for (int i = 0; i < 4; i++)
            {
                var go = roundPrefab != null ? Instantiate(roundPrefab) : GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Grenade";
                foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
                go.SetActive(false);
                rounds.Add(new Round { Visual = go, Trail = go.GetComponent<TrailRenderer>() });
            }
            if (explosionPrefab != null)
                for (int i = 0; i < 3; i++) blasts.Add(Instantiate(explosionPrefab));
        }

        private void Start()
        {
            loadout = GetComponent<WeaponLoadout>();
        }

        /// <summary>The selected weapon carries the launcher.</summary>
        public bool Available =>
            loadout != null && loadout.Muzzle != null && loadout.SelectedName != null &&
            loadout.SelectedName.IndexOf(mountOn, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>How far below the Support marker the left hand sits: the Bizon's own grip, nothing added.</summary>
        public float SupportDrop => 0.025f;

        /// <summary>Front of the weapon's own tube, where rounds leave (metres, unaffected by the model's scale).</summary>
        public Vector3 LaunchPosition => Available
            ? loadout.Muzzle.position + loadout.Muzzle.rotation * tubeOffset
            : transform.position + Vector3.up * 1.4f;

        public void SetCamera(ThirdPersonCamera rig) => cameraRig = rig;

        private void Update()
        {
            float dt = Time.deltaTime;
            SimulateRounds(dt);

            if (input.SwitchLauncherRequested)
            {
                input.SwitchLauncherRequested = false;
                Active = !Active && Available;
                switchReady = Time.time + switchTime;
                triggerWasDown = input.Fire; // don't fire on the switch press
            }
            if (!Available) Active = false;
            if (rifle != null) rifle.Suppressed = Active;

            if (Reloading && Time.time >= reloadEnd)
            {
                reloadEnd = 0f;
                Loaded = true;
                reserve--;
                Play(loadClip, 0.7f);
            }

            bool trigger = input.Fire;
            bool pressed = trigger && !triggerWasDown;
            triggerWasDown = trigger;
            if (!Active) return;
            if (input.ReloadRequested)
            {
                input.ReloadRequested = false; // R in launcher mode is the launcher's
                StartReload();
            }
            if (pressed && Loaded && Time.time >= switchReady) Launch();
        }

        private void StartReload()
        {
            if (Loaded || Reloading || reserve <= 0) return;
            reloadEnd = Time.time + reloadTime;
        }

        private void Launch()
        {
            if (!Available || cameraRig == null) return;
            Vector3 from = LaunchPosition;
            Vector3 target = rifle != null ? rifle.AimPoint : cameraRig.AimRay.GetPoint(60f);
            Vector3 v = SolveLaunch(from, target, muzzleSpeed, gravity);

            Round r = null;
            foreach (var x in rounds)
                if (!x.Live) { r = x; break; }
            if (r == null) r = rounds[0]; // all in the air: recycle the oldest slot
            r.Pos = from;
            r.Vel = v;
            r.Age = 0f;
            r.Travelled = 0f;
            r.Live = true;
            r.Visual.transform.SetPositionAndRotation(from, Quaternion.LookRotation(v));
            r.Visual.SetActive(true);
            if (r.Trail != null) r.Trail.Clear();

            Loaded = false;
            if (aimRig != null) aimRig.AddRecoil(recoilPitch, 0f);
            cameraRig.AddRecoil(cameraKick, 0f);
            if (shake != null) shake.Add(0.05f);
            Play(launchClip, 0.9f);
            Launched?.Invoke();
            StartReload();
        }

        /// <summary>Initial velocity to hit <paramref name="to"/>: the low arc if in range, else 45° towards it.</summary>
        public static Vector3 SolveLaunch(Vector3 from, Vector3 to, float speed, float g)
        {
            Vector3 d = to - from;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            float x = flat.magnitude, y = d.y;
            if (x < 0.01f) return (d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.up) * speed;
            Vector3 dir = flat / x;
            float v2 = speed * speed;
            float disc = v2 * v2 - g * (g * x * x + 2f * y * v2);
            float angle = disc >= 0f ? Mathf.Atan((v2 - Mathf.Sqrt(disc)) / (g * x)) : Mathf.PI / 4f;
            return (dir * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * speed;
        }

        private void SimulateRounds(float dt)
        {
            int live = 0;
            foreach (var r in rounds)
            {
                if (!r.Live) continue;
                r.Age += dt;
                Vector3 g = Vector3.down * gravity;
                Vector3 next = r.Pos + r.Vel * dt + 0.5f * dt * dt * g;
                Vector3 step = next - r.Pos;
                float len = step.magnitude;
                if (len > 1e-5f && Physics.SphereCast(r.Pos, roundRadius, step / len, out RaycastHit hit, len, hitMask, QueryTriggerInteraction.Ignore))
                {
                    r.Travelled += hit.distance;
                    Vector3 at = hit.point + hit.normal * 0.15f;
                    if (r.Travelled >= armingDistance) Detonate(at);
                    Retire(r);
                    continue;
                }
                r.Pos = next;
                r.Vel += g * dt;
                r.Travelled += len;
                r.Visual.transform.SetPositionAndRotation(r.Pos, Quaternion.LookRotation(r.Vel));
                if (r.Age > maxFlightTime)
                {
                    Retire(r);
                    continue;
                }
                live++;
            }
            RoundsInFlight = live;
        }

        private void Retire(Round r)
        {
            r.Live = false;
            r.Visual.SetActive(false);
        }

        /// <summary>Set off a blast here (also used by tests and debug tools).</summary>
        public ExplosionReport Detonate(Vector3 at)
        {
            var report = Explosions.Explode(at, blastRadius, blastDamage, blastForce, hitMask, this);
            if (blasts.Count > 0)
            {
                var fx = blasts[nextBlast];
                nextBlast = (nextBlast + 1) % blasts.Count;
                fx.Play(at);
            }
            if (shake != null)
            {
                float d = Vector3.Distance(shake.transform.position, at);
                shake.Add(Mathf.Clamp01(1f - d / 45f) * 0.35f);
            }
            Exploded?.Invoke(at, report);
            return report;
        }

        /// <summary>Back to a full launcher (the range reset).</summary>
        public void Restock()
        {
            reserve = startReserve;
            Loaded = true;
            reloadEnd = 0f;
        }

        private void Play(AudioClip clip, float volume)
        {
            if (audioSource != null && clip != null) audioSource.PlayOneShot(clip, volume);
        }
    }
}
