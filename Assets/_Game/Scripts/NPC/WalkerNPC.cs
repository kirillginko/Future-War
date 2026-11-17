using System;
using System.Collections.Generic;
using ForestSoldier.Combat;
using UnityEngine;

namespace ForestSoldier.NPC
{
    /// <summary>
    /// A soldier who walks a fixed route and can be shot. No AI: it doesn't
    /// see or fight the player. It follows the ground, pauses at the ends of
    /// a ping-pong route and when the player blocks its way, and staggers when
    /// hit. At zero health it plays one of the model's two death animations
    /// (shot from the front: "Dying"; from behind: "Death From Back Headshot")
    /// and stays down until the targets are reset.
    ///
    /// The body capsule on this object takes normal damage; a Hitbox on the
    /// head bone takes double (critical).
    /// </summary>
    public class WalkerNPC : MonoBehaviour, IHitZoneOwner, ISurface
    {
        [SerializeField] private Animator animator;
        [SerializeField] private float maxHealth = 100f;

        [Header("Route (world X, Z)")]
        [SerializeField] private Vector2[] route = Array.Empty<Vector2>();
        [Tooltip("Walk the route as a loop; otherwise walk it back and forth.")]
        [SerializeField] private bool loop;
        [Tooltip("Where on the route to start, 0..1.")]
        [Range(0f, 1f)] [SerializeField] private float startAt;
        [SerializeField] private float walkSpeed = 0.99f;
        [SerializeField] private float turnSpeed = 150f;
        [SerializeField] private float pauseAtEnds = 2f;

        [Header("Reactions")]
        [SerializeField] private float staggerTime = 0.35f;
        [SerializeField] private Renderer[] flashRenderers;
        [SerializeField] private Color flashColor = new Color(1f, 0.55f, 0.45f);
        [SerializeField] private float flashTime = 0.15f;
        [Tooltip("Stop when the player stands this close in front.")]
        [SerializeField] private float giveWay = 1.4f;

        private static readonly List<WalkerNPC> all = new List<WalkerNPC>();
        public static IReadOnlyList<WalkerNPC> All => all;
        public static event Action<WalkerNPC> StateChanged;
        public event Action<WalkerNPC, DamageInfo, DamageResult> Damaged;

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int DieFrontId = Animator.StringToHash("DieFront");
        private static readonly int DieBackId = Animator.StringToHash("DieBack");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public float Health { get; private set; }
        public float MaxHealth => maxHealth;
        public bool IsAlive => Health > 0f;
        public bool DiedFromBehind { get; private set; }
        public float CurrentSpeed { get; private set; }
        public SurfaceKind Surface => SurfaceKind.Flesh;

        private Collider[] colliders;
        private int next, dir = 1;
        private float pauseLeft, staggerLeft, flashLeft;
        private MaterialPropertyBlock block;
        private Color[] baseColors;
        private Transform player;
        private Terrain terrain;

        private void Awake()
        {
            Health = maxHealth;
            if (animator == null) animator = GetComponentInChildren<Animator>();
            colliders = GetComponentsInChildren<Collider>(true);
            block = new MaterialPropertyBlock();
            if (flashRenderers == null) flashRenderers = Array.Empty<Renderer>();
            baseColors = new Color[flashRenderers.Length];
            for (int i = 0; i < flashRenderers.Length; i++)
            {
                var m = flashRenderers[i].sharedMaterial;
                baseColors[i] = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
            }
        }

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        private void Start()
        {
            terrain = Terrain.activeTerrain;
            var motor = FindFirstObjectByType<ForestSoldier.Player.SoldierMotor>();
            if (motor != null) player = motor.transform;
            PlaceAtStart();
        }

        // ------------------------------------------------------------ route

        private float SegmentLength(int i) => Vector2.Distance(route[i], route[(i + 1) % route.Length]);

        private void PlaceAtStart()
        {
            if (route.Length < 2) return;
            int segments = loop ? route.Length : route.Length - 1;
            float total = 0f;
            for (int i = 0; i < segments; i++) total += SegmentLength(i);
            float d = Mathf.Clamp01(startAt) * total;
            int seg = 0;
            while (seg < segments - 1 && d > SegmentLength(seg))
            {
                d -= SegmentLength(seg);
                seg++;
            }
            Vector2 a = route[seg], b = route[(seg + 1) % route.Length];
            Vector2 p = Vector2.Lerp(a, b, SegmentLength(seg) > 0f ? d / SegmentLength(seg) : 0f);
            next = (seg + 1) % route.Length;
            dir = 1;
            Vector2 heading = b - a;
            transform.SetPositionAndRotation(OnGround(p),
                Quaternion.Euler(0f, Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg, 0f));
        }

        private Vector3 OnGround(Vector2 p)
        {
            float y = terrain != null ? terrain.SampleHeight(new Vector3(p.x, 0f, p.y)) + terrain.transform.position.y : transform.position.y;
            return new Vector3(p.x, y, p.y);
        }

        private void AdvanceWaypoint()
        {
            if (loop)
            {
                next = (next + 1) % route.Length;
                return;
            }
            int n = next + dir;
            if (n < 0 || n >= route.Length)
            {
                dir = -dir;
                n = next + dir;
                pauseLeft = pauseAtEnds;
            }
            next = n;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (flashLeft > 0f)
            {
                flashLeft = Mathf.Max(0f, flashLeft - dt);
                ApplyFlash(flashLeft / flashTime);
            }
            if (!IsAlive || route.Length < 2) return;

            float wanted = walkSpeed;
            if (pauseLeft > 0f)
            {
                pauseLeft -= dt;
                wanted = 0f;
            }
            if (staggerLeft > 0f)
            {
                staggerLeft -= dt;
                wanted = 0f;
            }
            if (player != null && wanted > 0f)
            {
                Vector3 toPlayer = player.position - transform.position;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude < giveWay * giveWay && Vector3.Dot(toPlayer, transform.forward) > 0f) wanted = 0f;
            }
            CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, wanted, 4f * dt);

            Vector2 pos = new Vector2(transform.position.x, transform.position.z);
            Vector2 to = route[next] - pos;
            if (to.magnitude < 0.25f)
            {
                AdvanceWaypoint();
                to = route[next] - pos;
            }
            if (to.sqrMagnitude > 1e-4f)
            {
                float yaw = Mathf.Atan2(to.x, to.y) * Mathf.Rad2Deg;
                float cur = transform.eulerAngles.y;
                transform.rotation = Quaternion.Euler(0f, Mathf.MoveTowardsAngle(cur, yaw, turnSpeed * dt), 0f);
                // Only walk once roughly facing the way: no moonwalking round corners.
                float facing = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(cur, yaw)) / 60f);
                pos += to.normalized * Mathf.Min(to.magnitude, CurrentSpeed * facing * dt);
            }
            transform.position = OnGround(pos);
            if (animator != null) animator.SetFloat(SpeedId, CurrentSpeed);
        }

        // ------------------------------------------------------------ damage

        public DamageResult ApplyDamage(in DamageInfo info) => ApplyDamage(info, 1f, false);

        public DamageResult ApplyDamage(in DamageInfo info, float multiplier, bool critical)
        {
            if (!IsAlive) return default;
            float amount = Mathf.Min(info.Amount * multiplier, Health);
            Health -= amount;
            flashLeft = flashTime;
            staggerLeft = staggerTime;
            bool killed = Health <= 0f;
            if (killed) Die(info);
            var result = new DamageResult(amount, killed, critical);
            Damaged?.Invoke(this, info, result);
            return result;
        }

        private void Die(in DamageInfo info)
        {
            Health = 0f;
            CurrentSpeed = 0f;
            // The round travels along info.Direction; if that's the way the walker faces, it came from behind.
            DiedFromBehind = Vector3.Dot(info.Direction, transform.forward) > 0f;
            foreach (var c in colliders) c.enabled = false;
            if (animator != null)
            {
                animator.SetFloat(SpeedId, 0f);
                animator.SetTrigger(DiedFromBehind ? DieBackId : DieFrontId);
            }
            StateChanged?.Invoke(this);
        }

        public void ResetWalker()
        {
            Health = maxHealth;
            pauseLeft = staggerLeft = flashLeft = 0f;
            CurrentSpeed = 0f;
            foreach (var c in colliders) c.enabled = true;
            ApplyFlash(0f);
            if (animator != null)
            {
                animator.Rebind();
                animator.Update(0f);
            }
            if (terrain == null) terrain = Terrain.activeTerrain;
            PlaceAtStart();
            StateChanged?.Invoke(this);
        }

        private void ApplyFlash(float t)
        {
            for (int i = 0; i < flashRenderers.Length; i++)
            {
                flashRenderers[i].GetPropertyBlock(block);
                block.SetColor(BaseColorId, Color.Lerp(baseColors[i], flashColor, t));
                flashRenderers[i].SetPropertyBlock(block);
            }
        }
    }
}
