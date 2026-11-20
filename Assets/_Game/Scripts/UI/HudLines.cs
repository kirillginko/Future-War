using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ForestSoldier.UI
{
    /// <summary>
    /// A UI graphic that draws line segments: a whole vector HUD (pitch ladder,
    /// tapes, reticles, target boxes) in one mesh and one draw call. Fill it each
    /// frame between <see cref="Begin"/> and <see cref="End"/>; coordinates are in
    /// the RectTransform's local space (the centre is 0, 0 for a centred pivot).
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class HudLines : MaskableGraphic
    {
        private struct Segment
        {
            public Vector2 A, B;
            public Color32 Colour;
            public float Width;
        }

        private readonly List<Segment> segments = new List<Segment>(512);

        public int Count => segments.Count;

        public void Begin() => segments.Clear();

        public void End() => SetVerticesDirty();

        public void Line(Vector2 a, Vector2 b, Color c, float width = 2f)
        {
            segments.Add(new Segment { A = a, B = b, Colour = c, Width = width });
        }

        /// <summary>A dashed line: <paramref name="dash"/> on, <paramref name="gap"/> off.</summary>
        public void Dashed(Vector2 a, Vector2 b, Color c, float dash, float gap, float width = 2f)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) return;
            Vector2 dir = (b - a) / len;
            for (float t = 0f; t < len; t += dash + gap)
                Line(a + dir * t, a + dir * Mathf.Min(len, t + dash), c, width);
        }

        /// <summary>A circle or an arc, degrees clockwise from the top.</summary>
        public void Arc(Vector2 centre, float radius, Color c, float from = 0f, float to = 360f, int steps = 32, float width = 2f)
        {
            float span = to - from;
            int n = Mathf.Max(2, Mathf.CeilToInt(steps * Mathf.Abs(span) / 360f));
            Vector2 prev = centre + Polar(radius, from);
            for (int i = 1; i <= n; i++)
            {
                Vector2 p = centre + Polar(radius, from + span * i / n);
                Line(prev, p, c, width);
                prev = p;
            }
        }

        public void Diamond(Vector2 centre, float size, Color c, float width = 2f)
        {
            Vector2 u = new Vector2(0f, size), r = new Vector2(size, 0f);
            Line(centre + u, centre + r, c, width);
            Line(centre + r, centre - u, c, width);
            Line(centre - u, centre - r, c, width);
            Line(centre - r, centre + u, c, width);
        }

        /// <summary>Four corner brackets around a box.</summary>
        public void Brackets(Vector2 centre, Vector2 half, float arm, Color c, float width = 2f)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                Vector2 k = centre + new Vector2(half.x * sx, half.y * sy);
                Line(k, k - new Vector2(arm * sx, 0f), c, width);
                Line(k, k - new Vector2(0f, arm * sy), c, width);
            }
        }

        public void Box(Vector2 centre, Vector2 half, Color c, float width = 2f)
        {
            Vector2 a = centre - half, b = centre + half;
            Line(new Vector2(a.x, a.y), new Vector2(b.x, a.y), c, width);
            Line(new Vector2(b.x, a.y), new Vector2(b.x, b.y), c, width);
            Line(new Vector2(b.x, b.y), new Vector2(a.x, b.y), c, width);
            Line(new Vector2(a.x, b.y), new Vector2(a.x, a.y), c, width);
        }

        private static Vector2 Polar(float r, float degFromTop)
        {
            float a = degFromTop * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var v = UIVertex.simpleVert;
            foreach (var s in segments)
            {
                Vector2 d = s.B - s.A;
                float len = d.magnitude;
                if (len < 1e-4f) continue;
                Vector2 n = new Vector2(-d.y, d.x) / len * (s.Width * 0.5f);
                // Stretch the ends by half a width so joined segments don't leave notches.
                Vector2 e = d / len * (s.Width * 0.5f);
                int i = vh.currentVertCount;
                v.color = s.Colour;
                v.position = s.A - e - n; vh.AddVert(v);
                v.position = s.A - e + n; vh.AddVert(v);
                v.position = s.B + e + n; vh.AddVert(v);
                v.position = s.B + e - n; vh.AddVert(v);
                vh.AddTriangle(i, i + 1, i + 2);
                vh.AddTriangle(i, i + 2, i + 3);
            }
        }
    }
}
