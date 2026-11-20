using System.Collections.Generic;
using ForestSoldier.Destruction;
using UnityEngine;

namespace ForestSoldier.Combat
{
    public struct ExplosionReport
    {
        public int Damaged;
        public int Killed;
        public int FragmentsBroken;
    }

    /// <summary>
    /// Blast resolution: damage with distance falloff to anything damageable in
    /// the radius that the blast can see, breaking of destructible structures,
    /// and a push on loose rigidbodies. Allocation-free apart from first use.
    /// </summary>
    public static class Explosions
    {
        private static readonly Collider[] hits = new Collider[256];
        private static readonly HashSet<IDamageable> damaged = new HashSet<IDamageable>();
        private static readonly HashSet<DestructibleStructure> structures = new HashSet<DestructibleStructure>();
        private static readonly HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();

        /// <param name="breach">How far it breaks structures, as a multiple of the usual reach (rockets punch bigger holes than grenades).</param>
        public static ExplosionReport Explode(Vector3 centre, float radius, float maxDamage, float force, int mask, Object source, float breach = 1f)
        {
            var report = new ExplosionReport();
            damaged.Clear();
            structures.Clear();
            pushed.Clear();

            int n = Physics.OverlapSphereNonAlloc(centre, radius, hits, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = hits[i];
                var structure = c.GetComponentInParent<DestructibleStructure>();
                if (structure != null)
                {
                    structures.Add(structure);
                    continue;
                }

                var d = c.GetComponentInParent<IDamageable>();
                // One hit per owner: a walker's head and body are the same walker.
                IDamageable owner = d is Hitbox ? c.GetComponentInParent<IHitZoneOwner>() : d;
                if (owner != null && owner.IsAlive && damaged.Add(owner))
                {
                    Vector3 at = c.bounds.center;
                    float dist = Vector3.Distance(centre, at);
                    if (!Shielded(centre, at, c, mask))
                    {
                        float k = 1f - Mathf.Clamp01(dist / radius);
                        float amount = maxDamage * k * k * 0.75f + maxDamage * 0.25f * k; // steep falloff
                        if (amount > 1f)
                        {
                            Vector3 dir = (at - centre).sqrMagnitude > 1e-4f ? (at - centre).normalized : Vector3.up;
                            // Blast push for whoever it kills: up to ~10 m/s at the centre, gone at the edge.
                            float impulse = force * 1.15f * k;
                            var r = owner.ApplyDamage(new DamageInfo(amount, at, -dir, dir, source, impulse, centre));
                            if (r.Landed) report.Damaged++;
                            if (r.Killed) report.Killed++;
                        }
                    }
                }

                var rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic && pushed.Add(rb))
                    rb.AddExplosionForce(force * rb.mass, centre, radius, 0.5f, ForceMode.Impulse);
            }

            foreach (var s in structures)
                report.FragmentsBroken += s.ExplodeAt(centre, radius * breach, force);
            return report;
        }

        /// <summary>A wall between the blast and the target soaks it up.</summary>
        private static bool Shielded(Vector3 centre, Vector3 target, Collider targetCollider, int mask)
        {
            Vector3 d = target - centre;
            float len = d.magnitude;
            if (len < 0.3f) return false;
            if (!Physics.Raycast(centre, d / len, out RaycastHit hit, len, mask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.collider == targetCollider) return false;
            // Hit something else first: shielded unless it belongs to the same thing.
            var a = hit.collider.GetComponentInParent<IHitZoneOwner>();
            var b = targetCollider.GetComponentInParent<IHitZoneOwner>();
            return a == null || a != b;
        }
    }
}
