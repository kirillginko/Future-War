using UnityEngine;

namespace ForestSoldier.Combat
{
    public struct ShotResult
    {
        public bool Hit;
        public RaycastHit Info;
        /// <summary>Where the tracer ends: the hit point, or the end of range.</summary>
        public Vector3 End;
        /// <summary>The crosshair pointed at something else; cover between the gun and it took the round.</summary>
        public bool Obstructed;
        /// <summary>What the crosshair was over.</summary>
        public Vector3 AimPoint;
    }

    /// <summary>
    /// Third-person hitscan: the camera decides what you aimed at, the gun
    /// decides what you hit. Pure physics queries, so it is testable on its own.
    /// </summary>
    public static class HitscanResolver
    {
        /// <summary>
        /// What the screen centre is over. The ray starts level with the shooter,
        /// so anything between the camera and the player is ignored.
        /// </summary>
        public static Vector3 AimPoint(Ray cameraRay, Vector3 shooter, float range, int mask, out RaycastHit hit, out bool didHit)
        {
            Vector3 dir = cameraRay.direction;
            float skip = Mathf.Max(0f, Vector3.Dot(shooter - cameraRay.origin, dir));
            Vector3 start = cameraRay.origin + dir * skip;
            didHit = Physics.Raycast(start, dir, out hit, range, mask, QueryTriggerInteraction.Ignore);
            return didHit ? hit.point : start + dir * range;
        }

        /// <param name="cameraRay">Ray through the crosshair (spread already applied).</param>
        /// <param name="muzzle">Barrel tip.</param>
        /// <param name="shooter">A point inside the shooter (shoulder height on the capsule axis);
        /// cover between it and the muzzle means the barrel is poking through a wall.</param>
        public static ShotResult Resolve(Ray cameraRay, Vector3 muzzle, Vector3 shooter, float range, int mask)
        {
            var r = new ShotResult();
            r.AimPoint = AimPoint(cameraRay, shooter, range, mask, out RaycastHit aimHit, out bool aimed);

            // Barrel through a wall: the wall takes it.
            if (Physics.Linecast(shooter, muzzle, out RaycastHit inWall, mask, QueryTriggerInteraction.Ignore))
            {
                r.Hit = true;
                r.Info = inWall;
                r.End = inWall.point;
                r.Obstructed = true;
                return r;
            }

            Vector3 toAim = r.AimPoint - muzzle;
            float dist = toAim.magnitude;
            if (dist < 1e-4f)
            {
                r.Hit = aimed;
                r.Info = aimHit;
                r.End = r.AimPoint;
                return r;
            }
            Vector3 dir = toAim / dist;
            // A little past the aim point so grazing hits on the aimed surface still register.
            float reach = aimed ? dist + 0.05f : Mathf.Min(range, dist);
            if (Physics.Raycast(muzzle, dir, out RaycastHit hit, reach, mask, QueryTriggerInteraction.Ignore))
            {
                r.Hit = true;
                r.Info = hit;
                r.End = hit.point;
                r.Obstructed = !aimed || hit.collider != aimHit.collider;
                return r;
            }
            r.End = muzzle + dir * reach;
            return r;
        }
    }
}
