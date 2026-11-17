using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// A range dummy on a hinge. Hits flash it and knock it back on a spring;
    /// at zero health it falls flat and its colliders switch off until reset.
    /// Hitboxes on child colliders forward damage here with a multiplier.
    /// The root's +Z should face the shooting position.
    /// </summary>
    public class DamageableTarget : MonoBehaviour, IHitZoneOwner
    {
        [SerializeField] private float maxHealth = 100f;
        [Tooltip("Rotates about its local X; everything that falls over is under it.")]
        [SerializeField] private Transform hinge;
        [SerializeField] private Renderer[] flashRenderers;
        [SerializeField] private Color flashColor = Color.white;
        [SerializeField] private float flashTime = 0.12f;

        [Header("Hit reaction")]
        [SerializeField] private float kickDegreesPerDamage = 0.35f;
        [SerializeField] private float maxKickDegrees = 22f;
        [SerializeField] private float springStiffness = 140f;
        [SerializeField] private float springDamping = 12f;

        [Header("Destroyed")]
        [SerializeField] private float fallDegrees = 82f;
        [SerializeField] private float fallTime = 0.35f;

        private static readonly List<DamageableTarget> all = new List<DamageableTarget>();
        public static IReadOnlyList<DamageableTarget> All => all;
        /// <summary>Raised when any target dies or is reset.</summary>
        public static event Action<DamageableTarget> StateChanged;

        public event Action<DamageableTarget, DamageInfo, DamageResult> Damaged;

        public float Health { get; private set; }
        public float MaxHealth => maxHealth;
        public bool IsAlive => Health > 0f;

        private Collider[] colliders;
        private float angle, angularVelocity, fallProgress, flashTimer;
        private MaterialPropertyBlock block;
        private Color[] baseColors;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private void Awake()
        {
            Health = maxHealth;
            colliders = GetComponentsInChildren<Collider>(true);
            block = new MaterialPropertyBlock();
            if (flashRenderers == null || flashRenderers.Length == 0) flashRenderers = GetComponentsInChildren<Renderer>();
            baseColors = new Color[flashRenderers.Length];
            for (int i = 0; i < flashRenderers.Length; i++)
            {
                var m = flashRenderers[i].sharedMaterial;
                baseColors[i] = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
            }
        }

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        public DamageResult ApplyDamage(in DamageInfo info) => ApplyDamage(info, 1f, false);

        public DamageResult ApplyDamage(in DamageInfo info, float multiplier, bool critical)
        {
            if (!IsAlive) return default;
            float amount = Mathf.Min(info.Amount * multiplier, Health);
            Health -= amount;
            flashTimer = flashTime;

            // Knock the top away from whoever shot it (positive angle; see Pose()).
            float push = Vector3.Dot(info.Direction, -transform.forward) >= 0f ? 1f : -1f;
            angularVelocity += push * Mathf.Min(amount * kickDegreesPerDamage, maxKickDegrees) * springDamping;

            bool killed = Health <= 0f;
            if (killed)
            {
                Health = 0f;
                fallProgress = 0f;
                foreach (var c in colliders) c.enabled = false;
                StateChanged?.Invoke(this);
            }
            var result = new DamageResult(amount, killed, critical);
            Damaged?.Invoke(this, info, result);
            return result;
        }

        public void ResetTarget()
        {
            Health = maxHealth;
            angle = angularVelocity = 0f;
            fallProgress = 0f;
            flashTimer = 0f;
            foreach (var c in colliders) c.enabled = true;
            Pose();
            ApplyFlash(0f);
            StateChanged?.Invoke(this);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (IsAlive)
            {
                // damped spring back to upright
                angularVelocity += (-springStiffness * angle - springDamping * angularVelocity) * dt;
                angle += angularVelocity * dt;
            }
            else
            {
                fallProgress = Mathf.Min(1f, fallProgress + dt / fallTime);
                float eased = 1f - (1f - fallProgress) * (1f - fallProgress) * (1f - fallProgress);
                angle = Mathf.Lerp(angle, fallDegrees, eased);
            }
            Pose();

            if (flashTimer > 0f)
            {
                flashTimer = Mathf.Max(0f, flashTimer - dt);
                ApplyFlash(flashTimer / flashTime);
            }
        }

        // Positive angle tips the top away from the shooter (towards -Z).
        private void Pose()
        {
            if (hinge != null) hinge.localRotation = Quaternion.Euler(-angle, 0f, 0f);
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
