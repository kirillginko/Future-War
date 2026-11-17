using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// Muzzle flash, tracers, impact bursts and weapon sounds. Everything is
    /// pooled up front; nothing allocates per shot.
    /// </summary>
    public class WeaponEffects : MonoBehaviour
    {
        [Header("Muzzle flash")]
        [Tooltip("Child of the muzzle holding the flash quads (and an optional light). Hidden between shots.")]
        [SerializeField] private Transform flash;
        [SerializeField] private Light flashLight;
        [SerializeField] private float flashTime = 0.045f;
        [SerializeField] private Vector2 flashScale = new Vector2(0.8f, 1.25f);

        [Header("Tracers")]
        [SerializeField] private Material tracerMaterial;
        [SerializeField] private Color tracerColor = new Color(1f, 0.85f, 0.5f, 0.9f);
        [SerializeField] private float tracerWidth = 0.02f;
        [SerializeField] private float tracerLife = 0.06f;
        [Tooltip("Tracer starts this far down the barrel line so it reads as a streak, not a beam.")]
        [SerializeField] private float tracerLength = 6f;
        [SerializeField] private int tracerPool = 12;

        [Header("Impacts")]
        [SerializeField] private ParticleSystem impactPrefab;
        [SerializeField] private int impactPool = 8;
        [SerializeField] private int particlesPerImpact = 10;

        [Header("Blood (flesh hits)")]
        [SerializeField] private ParticleSystem bloodPrefab;
        [SerializeField] private int bloodPool = 6;
        [SerializeField] private int dropletsPerHit = 16;
        [SerializeField] private Material splatMaterial;
        [SerializeField] private int splatPool = 40;
        [SerializeField] private Vector2 splatSize = new Vector2(0.3f, 0.6f);
        [SerializeField] private float killPoolSize = 1.5f;
        [Tooltip("How far behind the hit blood can land on a wall or tree.")]
        [SerializeField] private float splatReach = 2.5f;
        [SerializeField] private float splatLife = 30f;
        [SerializeField] private float splatFade = 6f;
        [SerializeField] private LayerMask splatMask = ~0;

        [Header("Audio")]
        [SerializeField] private AudioSource source;
        [SerializeField] private AudioClip shot;
        [SerializeField] private AudioClip[] shotVariants = System.Array.Empty<AudioClip>();
        [SerializeField] private AudioClip dryFire;
        [SerializeField] private AudioClip reloadSound;
        [SerializeField] private AudioClip reloadClack;
        [SerializeField] private AudioClip hitConfirm;
        [Tooltip("Hitting a person (anything whose ISurface is Flesh) instead of the metal ping.")]
        [SerializeField] private AudioClip fleshHit;
        [SerializeField] private AudioClip fleshKill;
        [SerializeField] private float pitchJitter = 0.05f;
        [Range(0f, 1f)] [SerializeField] private float shotVolume = 0.7f;
        [Range(0f, 1f)] [SerializeField] private float hitVolume = 0.5f;

        private float flashTimer;
        private float flashLightIntensity;

        private class Tracer
        {
            public LineRenderer Line;
            public float Age;
        }

        private readonly List<Tracer> tracers = new List<Tracer>();
        private readonly List<ParticleSystem> impacts = new List<ParticleSystem>();
        private readonly List<ParticleSystem> bloods = new List<ParticleSystem>();
        private int nextBlood;

        private class Splat
        {
            public Transform T;
            public Renderer R;
            public float Age = float.MaxValue;
            public Color Color;
        }

        private readonly List<Splat> splats = new List<Splat>();
        private int nextSplat;
        private MaterialPropertyBlock splatBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public int ActiveSplats { get; private set; }
        private int nextTracer, nextImpact;
        private int lastShotVariant = -1;
        private readonly List<float> reloadClacks = new List<float>(3);

        private void Awake()
        {
            shotVariants = System.Array.FindAll(shotVariants, clip => clip != null);
            if (flashLight != null) flashLightIntensity = flashLight.intensity;
            SetFlash(false);

            var tracerRoot = new GameObject("Tracers").transform;
            tracerRoot.SetParent(null, false);
            for (int i = 0; i < tracerPool; i++)
            {
                var go = new GameObject("Tracer");
                go.transform.SetParent(tracerRoot, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.sharedMaterial = tracerMaterial;
                lr.widthMultiplier = tracerWidth;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.numCapVertices = 2;
                lr.enabled = false;
                tracers.Add(new Tracer { Line = lr, Age = float.MaxValue });
            }

            if (impactPrefab != null)
            {
                var impactRoot = new GameObject("Impacts").transform;
                for (int i = 0; i < impactPool; i++)
                {
                    var ps = Instantiate(impactPrefab, impactRoot);
                    var main = ps.main;
                    main.playOnAwake = false;
                    impacts.Add(ps);
                }
            }

            if (bloodPrefab != null)
            {
                var bloodRoot = new GameObject("Blood").transform;
                for (int i = 0; i < bloodPool; i++)
                {
                    var ps = Instantiate(bloodPrefab, bloodRoot);
                    var main = ps.main;
                    main.playOnAwake = false;
                    bloods.Add(ps);
                }
            }

            if (splatMaterial != null)
            {
                splatBlock = new MaterialPropertyBlock();
                var splatRoot = new GameObject("BloodSplats").transform;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                var mesh = quad.GetComponent<MeshFilter>().sharedMesh;
                Destroy(quad);
                for (int i = 0; i < splatPool; i++)
                {
                    var go = new GameObject("Splat");
                    go.transform.SetParent(splatRoot, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = splatMaterial;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.enabled = false;
                    splats.Add(new Splat { T = go.transform, R = r, Color = splatMaterial.GetColor(BaseColorId) });
                }
            }
        }

        public void PlayShot(Transform muzzle, ShotResult shot, bool hitDamageable) =>
            PlayShot(muzzle, shot, hitDamageable ? new DamageResult(1f, false, false) : default);

        public void PlayShot(Transform muzzle, ShotResult shot, DamageResult result)
        {
            var surface = shot.Hit ? shot.Info.collider.GetComponentInParent<ISurface>() : null;
            bool flesh = surface != null && surface.Surface == SurfaceKind.Flesh;
            // flash
            flashTimer = flashTime;
            if (flash != null)
            {
                flash.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                flash.localScale = Vector3.one * Random.Range(flashScale.x, flashScale.y);
            }
            SetFlash(true);

            // tracer: a streak that ends at the impact
            var t = tracers[nextTracer];
            nextTracer = (nextTracer + 1) % tracers.Count;
            Vector3 end = shot.End;
            Vector3 dir = end - muzzle.position;
            float len = dir.magnitude;
            Vector3 start = len > tracerLength ? end - dir / len * tracerLength : muzzle.position;
            t.Line.SetPosition(0, start);
            t.Line.SetPosition(1, end);
            t.Line.enabled = true;
            t.Age = 0f;
            SetTracerAlpha(t, 1f);

            if (flesh) Bleed(shot, (shot.End - muzzle.position).normalized, result.Killed);
            else if (shot.Hit && impacts.Count > 0)
            {
                var ps = impacts[nextImpact];
                nextImpact = (nextImpact + 1) % impacts.Count;
                ps.transform.SetPositionAndRotation(shot.Info.point + shot.Info.normal * 0.01f,
                    Quaternion.LookRotation(shot.Info.normal));
                ps.Emit(particlesPerImpact);
            }

            Play(ChooseShot(), shotVolume);
            if (result.Landed)
            {
                if (flesh) Play(result.Killed && fleshKill != null ? fleshKill : fleshHit, hitVolume * 1.3f);
                else Play(hitConfirm, hitVolume);
            }
        }

        // ------------------------------------------------------------ blood

        private void Bleed(ShotResult shot, Vector3 dir, bool killed)
        {
            Vector3 p = shot.Info.point;
            if (bloods.Count > 0)
            {
                var ps = bloods[nextBlood];
                nextBlood = (nextBlood + 1) % bloods.Count;
                // Spray out of the far side, the way the round was going.
                ps.transform.SetPositionAndRotation(p, Quaternion.LookRotation(dir));
                ps.Emit(dropletsPerHit);
            }
            if (splats.Count == 0) return;

            // Behind the target: a wall or a trunk within reach, else the ground under the hit.
            if (Physics.Raycast(p + dir * 0.35f, dir, out RaycastHit wall, splatReach, splatMask, QueryTriggerInteraction.Ignore))
                PlaceSplat(wall.point, wall.normal, Random.Range(splatSize.x, splatSize.y));
            else if (Physics.Raycast(p + dir * 0.6f, Vector3.down, out RaycastHit ground, 3f, splatMask, QueryTriggerInteraction.Ignore))
                PlaceSplat(ground.point, ground.normal, Random.Range(splatSize.x, splatSize.y));

            // A body leaves a pool where it falls.
            if (killed && Physics.Raycast(new Vector3(p.x, p.y + 0.5f, p.z), Vector3.down, out RaycastHit under, 4f, splatMask, QueryTriggerInteraction.Ignore))
                PlaceSplat(under.point, under.normal, killPoolSize * Random.Range(0.85f, 1.15f));
        }

        private void PlaceSplat(Vector3 point, Vector3 normal, float size)
        {
            var s = splats[nextSplat];
            nextSplat = (nextSplat + 1) % splats.Count;
            // The quad's face points along -Z: turn it to face out of the surface, any roll.
            s.T.SetPositionAndRotation(point + normal * 0.012f,
                Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            s.T.localScale = new Vector3(size, size * Random.Range(0.75f, 1.25f), 1f);
            s.Age = 0f;
            s.R.enabled = true;
            SetSplatAlpha(s, 1f);
        }

        private void SetSplatAlpha(Splat s, float a)
        {
            s.R.GetPropertyBlock(splatBlock);
            var c = s.Color;
            c.a *= a;
            splatBlock.SetColor(BaseColorId, c);
            s.R.SetPropertyBlock(splatBlock);
        }

        private void UpdateSplats(float dt)
        {
            int active = 0;
            foreach (var s in splats)
            {
                if (!s.R.enabled) continue;
                s.Age += dt;
                if (s.Age >= splatLife)
                {
                    s.R.enabled = false;
                    continue;
                }
                active++;
                float fadeStart = splatLife - splatFade;
                if (s.Age > fadeStart) SetSplatAlpha(s, 1f - (s.Age - fadeStart) / splatFade);
            }
            ActiveSplats = active;
        }

        /// <summary>Wipe the blood (used when the range is reset).</summary>
        public void ClearBlood()
        {
            foreach (var s in splats) s.R.enabled = false;
            foreach (var b in bloods) b.Clear();
            ActiveSplats = 0;
        }

        private AudioClip ChooseShot()
        {
            if (shotVariants.Length == 0) return shot;
            if (shotVariants.Length == 1) return shotVariants[0];

            int index = Random.Range(0, lastShotVariant < 0 ? shotVariants.Length : shotVariants.Length - 1);
            if (lastShotVariant >= 0 && index >= lastShotVariant) index++;
            lastShotVariant = index;
            return shotVariants[index];
        }

        public void PlayDryFire() => Play(dryFire, 0.6f);

        /// <summary>Play the magazine recording at the same pace as the reload animation.</summary>
        public void PlayReload(float seconds)
        {
            reloadClacks.Clear();
            if (source != null && reloadSound != null && seconds > 0f)
            {
                source.pitch = reloadSound.length / seconds;
                source.PlayOneShot(reloadSound, 0.8f);
                return;
            }

            reloadClacks.Add(Time.time + seconds * 0.15f);
            reloadClacks.Add(Time.time + seconds * 0.55f);
            reloadClacks.Add(Time.time + seconds * 0.82f);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            UpdateSplats(dt);
            if (flashTimer > 0f)
            {
                flashTimer -= dt;
                if (flashTimer <= 0f) SetFlash(false);
            }
            foreach (var t in tracers)
            {
                if (!t.Line.enabled) continue;
                t.Age += dt;
                if (t.Age >= tracerLife) t.Line.enabled = false;
                else SetTracerAlpha(t, 1f - t.Age / tracerLife);
            }
            for (int i = reloadClacks.Count - 1; i >= 0; i--)
            {
                if (Time.time < reloadClacks[i]) continue;
                Play(reloadClack, 0.55f);
                reloadClacks.RemoveAt(i);
            }
        }

        private void SetTracerAlpha(Tracer t, float a)
        {
            var c = tracerColor;
            c.a *= a;
            var tail = c;
            tail.a = 0f;
            t.Line.startColor = tail;
            t.Line.endColor = c;
        }

        private void SetFlash(bool on)
        {
            if (flash != null) flash.gameObject.SetActive(on);
            if (flashLight != null) flashLight.intensity = on ? flashLightIntensity : 0f;
        }

        private void Play(AudioClip clip, float volume)
        {
            if (source == null || clip == null) return;
            source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            source.PlayOneShot(clip, volume);
        }
    }
}
