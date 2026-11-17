using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>Anything a weapon can hurt. Found with GetComponentInParent from the hit collider.</summary>
    public interface IDamageable
    {
        bool IsAlive { get; }
        DamageResult ApplyDamage(in DamageInfo info);
    }

    /// <summary>Something whose colliders carry <see cref="Hitbox"/>es that scale damage (heads, torsos).</summary>
    public interface IHitZoneOwner : IDamageable
    {
        DamageResult ApplyDamage(in DamageInfo info, float multiplier, bool critical);
    }

    /// <summary>What a hit sounds and looks like: sparks and a ping, or blood and a thud.</summary>
    public enum SurfaceKind { Hard, Flesh }

    /// <summary>Put on anything that isn't hard (walkers); found from the hit collider's parents.</summary>
    public interface ISurface
    {
        SurfaceKind Surface { get; }
    }

    public readonly struct DamageInfo
    {
        public readonly float Amount;
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        public readonly Vector3 Direction;
        public readonly Object Source;
        /// <summary>Blast push (m/s of velocity at the victim); 0 for bullets.</summary>
        public readonly float Impulse;
        /// <summary>Where a blast came from (valid when Impulse > 0).</summary>
        public readonly Vector3 BlastCentre;

        public DamageInfo(float amount, Vector3 point, Vector3 normal, Vector3 direction, Object source)
            : this(amount, point, normal, direction, source, 0f, point) { }

        public DamageInfo(float amount, Vector3 point, Vector3 normal, Vector3 direction, Object source, float impulse, Vector3 blastCentre)
        {
            Amount = amount;
            Point = point;
            Normal = normal;
            Direction = direction;
            Source = source;
            Impulse = impulse;
            BlastCentre = blastCentre;
        }
    }

    public readonly struct DamageResult
    {
        public readonly float Applied;
        public readonly bool Killed;
        /// <summary>Hit a critical zone (a target's head).</summary>
        public readonly bool Critical;

        public DamageResult(float applied, bool killed, bool critical)
        {
            Applied = applied;
            Killed = killed;
            Critical = critical;
        }

        public bool Landed => Applied > 0f;
    }
}
