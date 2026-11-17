using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Environment
{
    /// <summary>
    /// Fades tree canopies and bushes that the camera is inside or that stand
    /// between the camera and the player. Canopies carry trigger volumes on the
    /// Foliage layer; this sweeps a capsule from the camera to the player's
    /// chest and swaps the hit renderers' foliage material for a transparent
    /// twin, restoring it once they're clear. No per-frame allocations after
    /// the first time a renderer is seen.
    /// </summary>
    [DefaultExecutionOrder(1000)] // after the camera has been placed
    public class FoliageFader : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 targetOffset = new Vector3(0f, 1.3f, 0f);
        [SerializeField] private LayerMask foliageMask;
        [SerializeField] private float radius = 0.45f;
        [Tooltip("Opaque foliage materials, each matched by index to its faded twin.")]
        [SerializeField] private Material[] solid;
        [SerializeField] private Material[] faded;
        [Tooltip("Frames a renderer stays faded after it clears, so edges don't flicker.")]
        [SerializeField] private int holdFrames = 6;

        private class Entry
        {
            public Renderer Renderer;
            public Material[] Original;
            public Material[] Faded;
            public int LastSeen;
            public bool IsFaded;
        }

        private readonly Collider[] hits = new Collider[32];
        private readonly Dictionary<Collider, Entry> byCollider = new Dictionary<Collider, Entry>();
        private readonly List<Entry> active = new List<Entry>();
        private Dictionary<Material, Material> twins;
        private int frame;

        public int FadedCount { get; private set; }

        private void Awake()
        {
            twins = new Dictionary<Material, Material>();
            if (solid != null && faded != null)
                for (int i = 0; i < Mathf.Min(solid.Length, faded.Length); i++)
                    if (solid[i] != null && faded[i] != null) twins[solid[i]] = faded[i];
        }

        public void SetTarget(Transform t) => target = t;

        private void LateUpdate()
        {
            if (target == null) return;
            frame++;
            Vector3 a = transform.position;
            Vector3 b = target.position + targetOffset;
            int n = Physics.OverlapCapsuleNonAlloc(a, b, radius, hits, foliageMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var e = Lookup(hits[i]);
                if (e == null) continue;
                e.LastSeen = frame;
                if (!e.IsFaded)
                {
                    e.Renderer.sharedMaterials = e.Faded;
                    e.IsFaded = true;
                    active.Add(e);
                }
            }
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var e = active[i];
                if (frame - e.LastSeen <= holdFrames) continue;
                if (e.Renderer != null) e.Renderer.sharedMaterials = e.Original;
                e.IsFaded = false;
                active.RemoveAt(i);
            }
            FadedCount = active.Count;
        }

        private Entry Lookup(Collider c)
        {
            if (byCollider.TryGetValue(c, out var e)) return e;
            var r = c.GetComponentInParent<Renderer>();
            if (r == null)
            {
                byCollider[c] = null;
                return null;
            }
            var original = r.sharedMaterials;
            var fadedSet = new Material[original.Length];
            bool any = false;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != null && twins.TryGetValue(original[i], out var f))
                {
                    fadedSet[i] = f;
                    any = true;
                }
                else fadedSet[i] = original[i];
            }
            e = any ? new Entry { Renderer = r, Original = original, Faded = fadedSet } : null;
            byCollider[c] = e;
            return e;
        }

        private void OnDisable()
        {
            foreach (var e in active)
                if (e.Renderer != null) e.Renderer.sharedMaterials = e.Original;
            active.Clear();
            FadedCount = 0;
        }
    }
}
