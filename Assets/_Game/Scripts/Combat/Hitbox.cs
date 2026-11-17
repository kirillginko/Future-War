using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>Put on a collider under a DamageableTarget to scale the damage it takes (a head, say).</summary>
    public class Hitbox : MonoBehaviour, IDamageable
    {
        [SerializeField] private DamageableTarget owner;
        [SerializeField] private float multiplier = 1f;
        [SerializeField] private bool critical;

        public bool IsAlive => owner != null && owner.IsAlive;

        private void Awake()
        {
            if (owner == null) owner = GetComponentInParent<DamageableTarget>();
        }

        public DamageResult ApplyDamage(in DamageInfo info) =>
            owner != null ? owner.ApplyDamage(info, multiplier, critical) : default;
    }
}
