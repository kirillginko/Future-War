using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// Chunks torn off whatever a blast hits: a pooled set of small physics
    /// blocks thrown out from the surface, in that surface's own material (wall,
    /// rock, ground), that tumble, bounce, lie a while and shrink away. They sit
    /// on Ignore Raycast, so rounds and blasts pass through them.
    /// </summary>
    public class DebrisBurst : MonoBehaviour
    {
        [Tooltip("Used when the surface has no renderer of its own (the terrain): dirt.")]
        [SerializeField] private Material fallback;
        [SerializeField] private int poolSize = 120;
        [SerializeField] private float lifetime = 7f;
        [SerializeField] private Vector2 chunkSize = new Vector2(0.12f, 0.5f);

        private class Chunk
        {
            public Transform T;
            public Rigidbody Body;
            public MeshRenderer Renderer;
            public Vector3 Scale;
            public float Born;
        }

        private readonly List<Chunk> pool = new List<Chunk>();
        private int next;
        private Transform root;

        /// <summary>Chunks out in the world right now.</summary>
        public int Active { get; private set; }
        /// <summary>All chunks thrown so far.</summary>
        public int Thrown { get; private set; }

        private void Awake()
        {
            // At the scene root, so the chunks don't ride along with whatever owns this.
            root = new GameObject("Debris (" + name + ")").transform;
            for (int i = 0; i < poolSize; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Chunk";
                go.layer = 2; // Ignore Raycast: rounds and blasts pass through
                go.transform.SetParent(root, false);
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 4f;
                rb.linearDamping = 0.1f;
                rb.angularDamping = 0.3f;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                var r = go.GetComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.SetActive(false);
                pool.Add(new Chunk { T = go.transform, Body = rb, Renderer = r });
            }
        }

        private void OnDestroy()
        {
            if (root != null) Destroy(root.gameObject);
        }

        /// <summary>Throw <paramref name="count"/> chunks off <paramref name="surface"/> at <paramref name="at"/>.</summary>
        public void Spawn(Vector3 at, Vector3 normal, Collider surface, int count, float speed)
        {
            if (pool.Count == 0) return;
            Material mat = fallback;
            if (surface != null && !(surface is TerrainCollider))
            {
                var r = surface.GetComponentInParent<Renderer>();
                if (r != null && r.sharedMaterial != null) mat = r.sharedMaterial;
            }
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;
            for (int i = 0; i < count; i++)
            {
                var c = pool[next];
                next = (next + 1) % pool.Count;
                if (!c.T.gameObject.activeSelf) Active++;
                c.Scale = new Vector3(Random.Range(chunkSize.x, chunkSize.y), Random.Range(chunkSize.x, chunkSize.y), Random.Range(chunkSize.x, chunkSize.y));
                c.T.localScale = c.Scale;
                c.T.SetPositionAndRotation(at + normal * 0.25f + Random.insideUnitSphere * 0.4f, Random.rotation);
                if (mat != null) c.Renderer.sharedMaterial = mat;
                c.T.gameObject.SetActive(true);
                Vector3 dir = (normal + Random.insideUnitSphere * 0.9f + Vector3.up * 0.4f).normalized;
                c.Body.linearVelocity = dir * speed * Random.Range(0.35f, 1f);
                c.Body.angularVelocity = Random.insideUnitSphere * 12f;
                c.Born = Time.time;
                Thrown++;
            }
        }

        private void Update()
        {
            if (Active == 0) return;
            float now = Time.time;
            foreach (var c in pool)
            {
                if (!c.T.gameObject.activeSelf) continue;
                float age = now - c.Born;
                if (age > lifetime)
                {
                    c.T.gameObject.SetActive(false);
                    Active--;
                }
                else if (age > lifetime - 1f)
                    c.T.localScale = c.Scale * (lifetime - age); // shrink away over the last second
            }
        }
    }
}
