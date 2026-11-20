using System;
using System.Collections.Generic;
using ForestSoldier.Combat;
using ForestSoldier.NPC;
using ForestSoldier.Player;
using ForestSoldier.UI;
using UnityEngine;

namespace ForestSoldier.Vehicles
{
    /// <summary>
    /// The Little Bird's guns and rockets, from the barrels and tubes on its pods.
    ///
    /// Miniguns (fire: left mouse / RT): hitscan, alternating barrels, a short
    /// spin-up, aimed at the screen centre within a cone around the nose. They
    /// use the player's WeaponEffects for travelling streaks, impacts, blood and
    /// casings, and the HUD for hit markers.
    ///
    /// Target lock: a live target or walker within a few degrees of the
    /// crosshair, in sight and in range, is locked; the lock holds while it
    /// stays near the crosshair, and moves to another target that is clearly
    /// closer to it. While locked, the guns aim at it (within their
    /// swing), so you can fire on it without holding the crosshair dead on.
    ///
    /// Rockets (right mouse / LT): unguided, 14 in two pods, rippled while held.
    /// Simulated like the grenade (a sphere cast per frame along a gently
    /// dropping path) and they burst with the same blast: damage, destruction,
    /// ragdolls; they breach walls wider than a grenade and throw chunks of
    /// whatever they hit. The pods refill when the helicopter sits on the ground.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public class HelicopterWeapons : MonoBehaviour
    {
        [SerializeField] private HelicopterPilot pilot;
        [SerializeField] private HelicopterCamera chaseCamera;
        [SerializeField] private Transform[] gunMuzzles = Array.Empty<Transform>();
        [SerializeField] private Transform[] rocketTubes = Array.Empty<Transform>();
        [SerializeField] private Renderer[] gunFlashes = Array.Empty<Renderer>();
        [SerializeField] private LayerMask hitMask = ~0;

        [Header("Miniguns")]
        [SerializeField] private float gunRpm = 2400f;
        [SerializeField] private float gunDamage = 16f;
        [SerializeField] private float gunSpread = 0.9f;
        [SerializeField] private float gunRange = 400f;
        [SerializeField] private float spinUp = 0.25f;
        [Tooltip("The guns can swing this far off the nose to meet the crosshair.")]
        [SerializeField] private float gunCone = 25f;
        [SerializeField] private AudioSource gunAudio;

        [Header("Rockets")]
        [SerializeField] private int rocketCapacity = 14;
        [SerializeField] private float rocketInterval = 0.16f;
        [SerializeField] private float rocketSpeed = 140f;
        [SerializeField] private float rocketDrop = 3f;
        [SerializeField] private float rocketMaxTime = 5f;
        [SerializeField] private float rocketBlastRadius = 6f;
        [SerializeField] private float rocketDamage = 200f;
        [SerializeField] private float rocketForce = 13f;
        [Tooltip("How far a rocket breaks structures, as a multiple of a grenade-style blast of the same radius.")]
        [SerializeField] private float rocketBreach = 1.7f;
        [SerializeField] private DebrisBurst debris;
        [Tooltip("Chunks thrown off the surface a rocket hits (more off a structure it breaks).")]
        [SerializeField] private int debrisPerRocket = 12;
        [SerializeField] private float debrisSpeed = 10f;

        [Header("Target lock")]
        [Tooltip("A target this close to the crosshair (degrees) is locked.")]
        [SerializeField] private float lockAngle = 5f;
        [Tooltip("The lock holds until it is this far off the crosshair (degrees).")]
        [SerializeField] private float keepAngle = 10f;
        [SerializeField] private float lockRange = 350f;
        [Tooltip("The lock moves to another target under the crosshair if it is this many degrees closer to it.")]
        [SerializeField] private float switchMargin = 2f;
        [SerializeField] private float rearmAfter = 1.5f;
        [SerializeField] private GameObject rocketPrefab;
        [SerializeField] private ExplosionEffect explosionPrefab;
        [SerializeField] private AudioSource rocketAudio;
        [SerializeField] private AudioClip rocketLaunch;

        public int Rockets { get; private set; }
        public int RocketCapacity => rocketCapacity;
        public int ShotsFired { get; private set; }
        public int RocketsFired { get; private set; }
        public float Spin { get; private set; }

        public event Action<ShotResult, IDamageable, DamageResult> HitDamageable;
        public event Action<Vector3, ExplosionReport> RocketExploded;

        /// <summary>The locked target (null for none), and the point the guns aim at on it.</summary>
        public Transform LockedTarget { get; private set; }
        public Vector3 LockPoint { get; private set; }
        public DebrisBurst Debris => debris;
        /// <summary>Why the last lock was dropped (for tests and tuning).</summary>
        public string LockLostBecause { get; private set; } = "";

        private class Rocket
        {
            public GameObject Visual;
            public TrailRenderer Trail;
            public Vector3 Pos, Vel;
            public float Age;
            public bool Live;
        }

        private readonly List<Rocket> rockets = new List<Rocket>();
        private readonly List<ExplosionEffect> blasts = new List<ExplosionEffect>();
        private int nextBlast, nextGun, nextTube;
        private float nextShot, nextRocket, flashLeft, groundedFor;
        private WeaponEffects effects;
        private PrototypeHUD hud;
        private CameraShake shake;

        private void Awake()
        {
            if (pilot == null) pilot = GetComponent<HelicopterPilot>();
            Rockets = rocketCapacity;
            for (int i = 0; i < 8; i++)
            {
                var go = rocketPrefab != null ? Instantiate(rocketPrefab) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Rocket";
                foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
                go.SetActive(false);
                rockets.Add(new Rocket { Visual = go, Trail = go.GetComponent<TrailRenderer>() });
            }
            if (explosionPrefab != null)
                for (int i = 0; i < 4; i++) blasts.Add(Instantiate(explosionPrefab));
            foreach (var f in gunFlashes) if (f != null) f.enabled = false;
        }

        private void Start()
        {
            var input = FindFirstObjectByType<SoldierInput>();
            if (input != null) effects = input.GetComponent<WeaponEffects>();
            hud = FindFirstObjectByType<PrototypeHUD>();
            if (chaseCamera == null) chaseCamera = FindFirstObjectByType<HelicopterCamera>(FindObjectsInactive.Include);
            if (chaseCamera != null) shake = chaseCamera.GetComponent<CameraShake>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            SimulateRockets(dt);
            UpdateLock();
            if (flashLeft > 0f)
            {
                flashLeft -= dt;
                if (flashLeft <= 0f) foreach (var f in gunFlashes) if (f != null) f.enabled = false;
            }

            // Rearm on the ground.
            // On the ground: skid contact or just above it (a touchdown can bounce the skids), and still.
            bool down = (pilot.OnGround || pilot.Altitude < 0.4f) && pilot.Speed < 1.5f;
            groundedFor = down ? groundedFor + dt : 0f;
            if (groundedFor > rearmAfter) Rockets = rocketCapacity;

            var input = pilot.Input;
            bool flying = pilot.Occupied && input != null && chaseCamera != null && chaseCamera.Engaged;
            bool trigger = flying && input.Fire;
            Spin = Mathf.MoveTowards(Spin, trigger ? 1f : 0f, dt / spinUp);
            if (gunAudio != null)
            {
                bool loud = trigger && Spin >= 1f;
                if (loud && !gunAudio.isPlaying) gunAudio.Play();
                else if (!loud && gunAudio.isPlaying) gunAudio.Stop();
            }
            if (trigger && Spin >= 1f && gunMuzzles.Length > 0)
            {
                float interval = 60f / gunRpm;
                if (nextShot < Time.time - interval) nextShot = Time.time;
                while (Time.time >= nextShot)
                {
                    FireGun();
                    nextShot += interval;
                }
            }

            if (flying && input.Aim && Rockets > 0 && rocketTubes.Length > 0 && Time.time >= nextRocket)
            {
                FireRocket();
                nextRocket = Time.time + rocketInterval;
            }
        }

        /// <summary>The crosshair's point, as the camera sees it, ignoring the helicopter itself.</summary>
        public Vector3 AimPoint
        {
            get
            {
                Ray r = chaseCamera.AimRay;
                int mask = hitMask & ~(1 << gameObject.layer);
                return Physics.Raycast(r, out RaycastHit hit, gunRange, mask, QueryTriggerInteraction.Ignore) ? hit.point : r.GetPoint(gunRange);
            }
        }

        /// <summary>The guns' hidden aim limit: off-nose by up to this many degrees.</summary>
        public float GunCone => gunCone;
        public float GunRange => gunRange;

        /// <summary>Where the guns' rounds go right now (from between the barrels): the HUD's gun reticle.</summary>
        public Vector3 GunImpactPoint
        {
            get
            {
                if (gunMuzzles.Length == 0) return AimPoint;
                Vector3 mid = Vector3.zero;
                foreach (var m in gunMuzzles) mid += m.position;
                mid /= gunMuzzles.Length;
                Vector3 dir = AimFrom(mid);
                int mask = hitMask & ~(1 << gameObject.layer);
                return Physics.Raycast(mid, dir, out RaycastHit hit, gunRange, mask, QueryTriggerInteraction.Ignore) ? hit.point : mid + dir * gunRange;
            }
        }

        /// <summary>Where a rocket fired now would land (its dropping path traced ahead): the HUD's rocket marker.</summary>
        public bool PredictRocket(out Vector3 impact)
        {
            impact = default;
            if (rocketTubes.Length == 0) return false;
            Vector3 pos = Vector3.zero;
            foreach (var t in rocketTubes) pos += t.position;
            pos /= rocketTubes.Length;
            Vector3 vel = AimFrom(pos) * rocketSpeed + (pilot != null && pilot.Body != null ? pilot.Body.linearVelocity : Vector3.zero);
            int mask = hitMask & ~(1 << gameObject.layer);
            const float step = 0.05f;
            Vector3 g = Vector3.down * rocketDrop;
            for (float t = 0f; t < rocketMaxTime; t += step)
            {
                Vector3 next = pos + vel * step + 0.5f * step * step * g;
                Vector3 seg = next - pos;
                if (Physics.Raycast(pos, seg, out RaycastHit hit, seg.magnitude, mask, QueryTriggerInteraction.Ignore))
                {
                    impact = hit.point;
                    return true;
                }
                pos = next;
                vel += g * step;
            }
            impact = pos; // bursts in the air at the end of its motor
            return false;
        }

        // ---------------------------------------------------------------- lock

        private void UpdateLock()
        {
            bool flying = pilot.Occupied && chaseCamera != null && chaseCamera.Engaged;
            if (!flying) { LockedTarget = null; return; }
            Ray aim = chaseCamera.AimRay;

            // Hold the current lock while it stays within keepAngle of the crosshair...
            float held = 180f;
            if (LockedTarget != null)
            {
                if (Candidate(LockedTarget, aim, keepAngle, out Vector3 p, out float a)) { LockPoint = p; held = a; }
                else
                {
                    LockLostBecause = $"{LockedTarget.name}: {why}";
                    LockedTarget = null;
                }
            }

            // ...but move it to whatever is under the crosshair, if that is clearly closer to it.
            float best = Mathf.Min(lockAngle, held - switchMargin);
            Transform found = null;
            Vector3 foundPoint = default;
            foreach (var t in DamageableTarget.All)
                if (t != null && t.IsAlive && t.transform != LockedTarget && Candidate(t.transform, aim, best, out Vector3 p, out float a))
                { best = a; found = t.transform; foundPoint = p; }
            foreach (var w in WalkerNPC.All)
                if (w != null && w.IsAlive && w.isActiveAndEnabled && w.transform != LockedTarget && Candidate(w.transform, aim, best, out Vector3 p, out float a))
                { best = a; found = w.transform; foundPoint = p; }
            if (found != null)
            {
                LockedTarget = found;
                LockPoint = foundPoint;
            }
        }

        /// <summary>The point to hit on a target: the middle of a range target's board (its biggest part), a walker's chest.</summary>
        public static Vector3 TargetPoint(Transform t)
        {
            if (t.GetComponent<DamageableTarget>() != null)
            {
                Collider biggest = null;
                float most = 0f;
                foreach (var c in t.GetComponentsInChildren<Collider>())
                {
                    if (!c.enabled) continue;
                    Vector3 e = c.bounds.size;
                    float v = e.x * e.y + e.y * e.z + e.z * e.x; // by face area: the board, not the post
                    if (v > most) { most = v; biggest = c; }
                }
                if (biggest != null) return biggest.bounds.center;
            }
            return t.position + Vector3.up * 1.2f;
        }

        private string why = "";

        private bool Candidate(Transform t, Ray aim, float within, out Vector3 point, out float angle)
        {
            point = default;
            angle = 180f;
            // Cheap first look: nowhere near the crosshair, skip it.
            if (Vector3.Angle(aim.direction, t.position + Vector3.up - aim.origin) > within + 12f) { why = "far off"; return false; }
            point = TargetPoint(t);
            if (!t.gameObject.activeInHierarchy) { why = "gone"; return false; }
            if (t.TryGetComponent(out DamageableTarget dt) && !dt.IsAlive) { why = "down"; return false; }
            if (t.TryGetComponent(out WalkerNPC wk) && (!wk.IsAlive || !wk.isActiveAndEnabled)) return false;
            Vector3 to = point - aim.origin;
            float dist = to.magnitude;
            if (dist > lockRange || dist < 1f) { why = "range"; return false; }
            angle = Vector3.Angle(aim.direction, to);
            if (angle >= within) { why = $"angle {angle:F1}"; return false; }
            // In sight: nothing else in the way (the helicopter itself doesn't count).
            int mask = hitMask & ~(1 << gameObject.layer);
            if (Physics.Raycast(aim.origin, to / dist, out RaycastHit hit, dist + 0.5f, mask, QueryTriggerInteraction.Ignore)
                && !hit.collider.transform.IsChildOf(t) && hit.distance < dist - 0.6f)
            {
                why = $"blocked by {hit.collider.name} at {hit.distance:F1} of {dist:F1} m";
                return false;
            }
            return true;
        }

        private Vector3 AimFrom(Vector3 origin)
        {
            // Locked: the guns go for the target; otherwise for the crosshair.
            Vector3 want = ((LockedTarget != null ? LockPoint : AimPoint) - origin).normalized;
            // Keep it inside a cone around the nose.
            float off = Vector3.Angle(transform.forward, want);
            return off <= gunCone ? want : Vector3.Slerp(transform.forward, want, gunCone / off).normalized;
        }

        private void FireGun()
        {
            var muzzle = gunMuzzles[nextGun];
            nextGun = (nextGun + 1) % gunMuzzles.Length;
            Vector3 dir = AimFrom(muzzle.position);
            Vector2 d = UnityEngine.Random.insideUnitCircle * Mathf.Tan(gunSpread * Mathf.Deg2Rad);
            dir = (Quaternion.LookRotation(dir) * new Vector3(d.x, d.y, 1f)).normalized;
            int mask = hitMask & ~(1 << gameObject.layer);
            var ray = new Ray(muzzle.position, dir);
            // The gun is the camera here: the round goes where the barrel points, so resolve from the muzzle.
            ShotResult shot = HitscanResolver.Resolve(ray, muzzle.position, muzzle.position, gunRange, mask);
            DamageResult result = default;
            IDamageable target = null;
            if (shot.Hit)
            {
                target = shot.Info.collider.GetComponentInParent<IDamageable>();
                if (target != null && target.IsAlive)
                    result = target.ApplyDamage(new DamageInfo(gunDamage, shot.Info.point, shot.Info.normal, dir, this));
            }
            ShotsFired++;
            muzzle.rotation = Quaternion.LookRotation(dir); // casings and streaks follow the barrel
            if (effects != null) effects.PlayRound(muzzle, shot, result, ShotsFired % 2 == 0);
            foreach (var f in gunFlashes) if (f != null) f.enabled = true;
            flashLeft = 0.03f;
            if (result.Landed)
            {
                if (hud != null) hud.ShowHit(shot, target, result);
                HitDamageable?.Invoke(shot, target, result);
            }
        }

        private void FireRocket()
        {
            var tube = rocketTubes[nextTube];
            nextTube = (nextTube + 1) % rocketTubes.Length;
            Rocket r = null;
            foreach (var x in rockets)
                if (!x.Live) { r = x; break; }
            if (r == null) r = rockets[0];
            Vector3 dir = AimFrom(tube.position);
            r.Pos = tube.position;
            r.Vel = dir * rocketSpeed + pilot.Body.linearVelocity;
            r.Age = 0f;
            r.Live = true;
            r.Visual.transform.SetPositionAndRotation(r.Pos, Quaternion.LookRotation(r.Vel));
            r.Visual.SetActive(true);
            if (r.Trail != null) r.Trail.Clear();
            Rockets--;
            RocketsFired++;
            if (rocketAudio != null && rocketLaunch != null) rocketAudio.PlayOneShot(rocketLaunch, 0.8f);
            if (shake != null) shake.Add(0.04f);
        }

        private void SimulateRockets(float dt)
        {
            int mask = hitMask & ~(1 << gameObject.layer);
            foreach (var r in rockets)
            {
                if (!r.Live) continue;
                r.Age += dt;
                Vector3 g = Vector3.down * rocketDrop;
                Vector3 next = r.Pos + r.Vel * dt + 0.5f * dt * dt * g;
                Vector3 step = next - r.Pos;
                float len = step.magnitude;
                if (len > 1e-5f && Physics.SphereCast(r.Pos, 0.06f, step / len, out RaycastHit hit, len, mask, QueryTriggerInteraction.Ignore))
                {
                    Burst(hit.point + hit.normal * 0.2f, hit.normal, hit.collider);
                    r.Live = false;
                    r.Visual.SetActive(false);
                    continue;
                }
                r.Pos = next;
                r.Vel += g * dt;
                r.Visual.transform.SetPositionAndRotation(r.Pos, Quaternion.LookRotation(r.Vel));
                if (r.Age > rocketMaxTime)
                {
                    Burst(r.Pos, Vector3.up, null); // self-destructs at the end of its motor
                    r.Live = false;
                    r.Visual.SetActive(false);
                }
            }
        }

        private void Burst(Vector3 at, Vector3 normal, Collider surface)
        {
            var report = Explosions.Explode(at, rocketBlastRadius, rocketDamage, rocketForce, hitMask & ~(1 << gameObject.layer), this, rocketBreach);
            // Tear chunks off what it hit: more where it broke a structure.
            if (debris != null && surface != null)
                debris.Spawn(at, normal, surface, debrisPerRocket + Mathf.Min(16, report.FragmentsBroken * 2), debrisSpeed);
            if (blasts.Count > 0)
            {
                blasts[nextBlast].Play(at);
                nextBlast = (nextBlast + 1) % blasts.Count;
            }
            if (shake != null && chaseCamera != null)
                shake.Add(Mathf.Clamp01(1f - Vector3.Distance(chaseCamera.transform.position, at) / 60f) * 0.3f);
            RocketExploded?.Invoke(at, report);
        }

        public void Rearm() => Rockets = rocketCapacity;
    }
}
