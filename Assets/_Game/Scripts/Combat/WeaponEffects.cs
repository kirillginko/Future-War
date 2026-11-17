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

        [Header("Audio")]
        [SerializeField] private AudioSource source;
        [SerializeField] private AudioClip shot;
        [SerializeField] private AudioClip dryFire;
        [SerializeField] private AudioClip reloadClack;
        [SerializeField] private AudioClip hitConfirm;
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
        private int nextTracer, nextImpact;
        private readonly List<float> reloadClacks = new List<float>(3);

        private void Awake()
        {
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
        }

        public void PlayShot(Transform muzzle, ShotResult shot, bool hitDamageable)
        {
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

            if (shot.Hit && impacts.Count > 0)
            {
                var ps = impacts[nextImpact];
                nextImpact = (nextImpact + 1) % impacts.Count;
                ps.transform.SetPositionAndRotation(shot.Info.point + shot.Info.normal * 0.01f,
                    Quaternion.LookRotation(shot.Info.normal));
                ps.Emit(particlesPerImpact);
            }

            Play(this.shot, shotVolume);
            if (hitDamageable) Play(hitConfirm, hitVolume);
        }

        public void PlayDryFire() => Play(dryFire, 0.6f);

        /// <summary>Mag out, mag in, bolt: three clacks spread over the reload.</summary>
        public void PlayReload(float seconds)
        {
            reloadClacks.Clear();
            reloadClacks.Add(Time.time + seconds * 0.15f);
            reloadClacks.Add(Time.time + seconds * 0.55f);
            reloadClacks.Add(Time.time + seconds * 0.82f);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
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
