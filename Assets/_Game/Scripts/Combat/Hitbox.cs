using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// Put on a collider under a target or a walker to scale the damage it
    /// takes (a head, say). The owner is any component implementing IHitZoneOwner.
    /// </summary>
    public class Hitbox : MonoBehaviour, IDamageable
    {
        [Tooltip("A component implementing IHitZoneOwner (DamageableTarget, WalkerNPC). Found in the parents if empty.")]
        [SerializeField] private MonoBehaviour owner;
        [SerializeField] private float multiplier = 1f;
        [SerializeField] private bool critical;

        private IHitZoneOwner zoneOwner;

        public bool IsAlive => zoneOwner != null && zoneOwner.IsAlive;
        public bool Critical => critical;

        private void Awake()
        {
            zoneOwner = owner as IHitZoneOwner;
            if (zoneOwner == null)
            {
                // The owner lives on a parent (or on this object; the hitbox may sit deep in a skeleton).
                for (var t = transform; t != null && zoneOwner == null; t = t.parent)
                    foreach (var mb in t.GetComponents<MonoBehaviour>())
                        if (mb is IHitZoneOwner o) { zoneOwner = o; break; }
            }
        }

        public DamageResult ApplyDamage(in DamageInfo info) =>
            zoneOwner != null ? zoneOwner.ApplyDamage(info, multiplier, critical) : default;
    }
}
