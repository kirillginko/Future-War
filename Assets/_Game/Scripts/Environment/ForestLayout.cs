using UnityEngine;

namespace ForestSoldier.Environment
{
    /// <summary>
    /// The forest's fixed layout, shared by the editor builder and the tests.
    /// World axes: +X east (down range), +Z north. Ground level in the
    /// clearings is y = 0.
    /// </summary>
    public static class ForestLayout
    {
        /// <summary>Terrain square, centred on the origin.</summary>
        public const float TerrainSize = 300f;
        /// <summary>How far below y = 0 the terrain can dip (the pond).</summary>
        public const float TerrainBase = 6f;
        public const float TerrainHeight = 40f;
        /// <summary>Invisible walls stand here (±), inside the rim hills.</summary>
        public const float Boundary = 132f;

        /// <summary>Flat cleared areas (x, z, width, depth) where the ground is exactly y = 0.</summary>
        public static readonly Rect TrainingYard = Rect.MinMaxRect(-30f, -35f, 30f, 30f);
        public static readonly Rect RangeLane = Rect.MinMaxRect(10f, -12f, 110f, 12f);
        /// <summary>A cleared hamlet on the west trail with destructible houses.</summary>
        public static readonly Rect Hamlet = Rect.MinMaxRect(-104f, -20f, -54f, 24f);

        /// <summary>House centres (x, z) and yaw; doors (local -Z) face the trail through the middle.</summary>
        public static readonly Vector3[] Houses =
        {
            new Vector3(-94f, 13f, -90f), new Vector3(-94f, -7f, -90f),
            new Vector3(-64f, 13f, 90f), new Vector3(-62f, -9f, 90f),
        };
        /// <summary>Metres over which a clearing blends into the rolling forest floor.</summary>
        public const float ClearingFalloff = 14f;
        /// <summary>No trees within this distance of a clearing's edge.</summary>
        public const float ClearingTreeMargin = 4f;

        /// <summary>The dirt trail: leaves the yard north, loops west and south, and reaches the range from the south.</summary>
        public static readonly Vector2[] Trail =
        {
            new Vector2(0f, 28f), new Vector2(-8f, 48f), new Vector2(-30f, 62f), new Vector2(-58f, 58f),
            new Vector2(-78f, 36f), new Vector2(-82f, 6f), new Vector2(-70f, -28f), new Vector2(-46f, -52f),
            new Vector2(-14f, -62f), new Vector2(20f, -58f), new Vector2(50f, -44f), new Vector2(78f, -30f),
            new Vector2(92f, -14f),
        };
        public const float TrailHalfWidth = 2.2f;

        public static readonly Vector2 PondCentre = new Vector2(-62f, -78f);
        public const float PondRadius = 13f;
        public const float WaterLevel = -0.9f;

        public static readonly Vector2 LookoutHill = new Vector2(-92f, 74f);
        public static readonly Vector2 ThreeStones = new Vector2(64f, -74f);

        /// <summary>Samples the trail as a polyline (Catmull-Rom through the waypoints).</summary>
        public static Vector2 TrailPoint(float t)
        {
            int n = Trail.Length - 1;
            float f = Mathf.Clamp01(t) * n;
            int i = Mathf.Min(Mathf.FloorToInt(f), n - 1);
            float u = f - i;
            Vector2 p0 = Trail[Mathf.Max(i - 1, 0)], p1 = Trail[i], p2 = Trail[i + 1], p3 = Trail[Mathf.Min(i + 2, n)];
            float u2 = u * u, u3 = u2 * u;
            return 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
        }

        /// <summary>Distance in metres from a point to the trail's centre line.</summary>
        public static float DistanceToTrail(Vector2 p, int samples = 400)
        {
            float best = float.MaxValue;
            Vector2 prev = TrailPoint(0f);
            for (int i = 1; i <= samples; i++)
            {
                Vector2 cur = TrailPoint(i / (float)samples);
                Vector2 seg = cur - prev;
                float t = seg.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - prev, seg) / seg.sqrMagnitude) : 0f;
                best = Mathf.Min(best, (p - (prev + seg * t)).magnitude);
                prev = cur;
            }
            return best;
        }

        /// <summary>Distance outside a rect (0 inside).</summary>
        public static float DistanceOutside(Rect r, Vector2 p)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static float DistanceToClearing(Vector2 p) =>
            Mathf.Min(Mathf.Min(DistanceOutside(TrainingYard, p), DistanceOutside(RangeLane, p)), DistanceOutside(Hamlet, p));
    }
}
