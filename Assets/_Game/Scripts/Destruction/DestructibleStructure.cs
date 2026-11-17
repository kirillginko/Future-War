using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Destruction
{
    /// <summary>
    /// A pre-fractured structure drawn as ONE skinned mesh: every fragment is a
    /// bone (weight 1), so the whole building costs one draw call intact or in
    /// pieces. Fragments carry static box colliders while attached. An explosion
    /// detaches the fragments it reaches (they get a Rigidbody and fly), then
    /// anything no longer connected to the ground through its neighbours falls
    /// too. Debris that has come to rest is frozen back into static rubble so it
    /// stops costing physics time.
    ///
    /// Built by the editor setup (fragments, neighbour graph, bind poses); see
    /// ForestSoldierSetup.Destruction.
    /// </summary>
    public class DestructibleStructure : MonoBehaviour
    {
        [SerializeField] private SkinnedMeshRenderer meshRenderer;
        [SerializeField] private Transform[] fragments = System.Array.Empty<Transform>();
        [Tooltip("Fragment box sizes in the structure's local space (metres).")]
        [SerializeField] private Vector3[] sizes = System.Array.Empty<Vector3>();
        [Tooltip("Rests on the ground: the roots of the support search.")]
        [SerializeField] private bool[] anchored = System.Array.Empty<bool>();
        [Tooltip("Neighbour lists, flattened: fragment i's neighbours are neighbourIndex[neighbourStart[i] .. neighbourStart[i+1]).")]
        [SerializeField] private int[] neighbourStart = System.Array.Empty<int>();
        [SerializeField] private int[] neighbourIndex = System.Array.Empty<int>();

        [Header("Physics")]
        [SerializeField] private float density = 900f; // kg/m³: light masonry, so chunks fly a bit
        [Tooltip("Fragments within this fraction of the blast radius break off.")]
        [Range(0.2f, 1f)] [SerializeField] private float breakFraction = 0.55f;
        [SerializeField] private float settleSeconds = 10f;
        [SerializeField] private float maxDebrisSeconds = 20f;

        private static readonly List<DestructibleStructure> all = new List<DestructibleStructure>();
        public static IReadOnlyList<DestructibleStructure> All => all;

        private bool[] detached;
        private Rigidbody[] bodies;
        private float[] detachTime;
        private Vector3[] restPos;
        private Quaternion[] restRot;
        private Bounds restBounds;
        private int activeDebris;
        private readonly Queue<int> queue = new Queue<int>();
        private bool[] supported;

        public int FragmentCount => fragments.Length;
        public int DetachedCount { get; private set; }
        public int ActiveDebris => activeDebris;
        public SkinnedMeshRenderer Renderer => meshRenderer;

        private void Awake()
        {
            int n = fragments.Length;
            detached = new bool[n];
            bodies = new Rigidbody[n];
            detachTime = new float[n];
            supported = new bool[n];
            restPos = new Vector3[n];
            restRot = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                restPos[i] = fragments[i].localPosition;
                restRot[i] = fragments[i].localRotation;
            }
            if (meshRenderer != null) restBounds = meshRenderer.localBounds;
        }

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        public bool IsDetached(int i) => detached[i];
        public Transform Fragment(int i) => fragments[i];

        /// <summary>Break what the blast reaches, then drop whatever lost its support.</summary>
        public int ExplodeAt(Vector3 centre, float radius, float force)
        {
            int before = DetachedCount;
            float reach = radius * breakFraction;
            for (int i = 0; i < fragments.Length; i++)
            {
                if (detached[i]) continue;
                // Distance from the blast to the fragment's box, not its centre: big slabs count.
                Vector3 local = fragments[i].InverseTransformPoint(centre);
                Vector3 half = sizes[i] * 0.5f;
                Vector3 clamped = new Vector3(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y), Mathf.Clamp(local.z, -half.z, half.z));
                float d = Vector3.Distance(fragments[i].TransformPoint(clamped), centre);
                if (d <= reach) Detach(i, centre, radius, force * (1f - d / Mathf.Max(reach, 1e-3f) * 0.6f));
            }
            DropUnsupported(centre, radius, force * 0.08f);
            return DetachedCount - before;
        }

        /// <summary>Attached fragments with no path to the ground (should always be 0; for tests).</summary>
        public int CountFloatingAttached()
        {
            MarkSupported();
            int floating = 0;
            for (int i = 0; i < fragments.Length; i++)
                if (!detached[i] && !supported[i]) floating++;
            return floating;
        }

        private void DropUnsupported(Vector3 centre, float radius, float nudge)
        {
            MarkSupported();
            for (int i = 0; i < fragments.Length; i++)
                if (!detached[i] && !supported[i]) Detach(i, centre, radius * 3f, nudge);
        }

        private void MarkSupported()
        {
            int n = fragments.Length;
            queue.Clear();
            for (int i = 0; i < n; i++)
            {
                supported[i] = !detached[i] && anchored[i];
                if (supported[i]) queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                for (int k = neighbourStart[i]; k < neighbourStart[i + 1]; k++)
                {
                    int j = neighbourIndex[k];
                    if (supported[j] || detached[j]) continue;
                    supported[j] = true;
                    queue.Enqueue(j);
                }
            }
        }

        private void Detach(int i, Vector3 centre, float radius, float force)
        {
            detached[i] = true;
            DetachedCount++;
            detachTime[i] = Time.time;
            var rb = fragments[i].gameObject.AddComponent<Rigidbody>();
            Vector3 s = sizes[i];
            rb.mass = Mathf.Max(2f, s.x * s.y * s.z * density);
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.2f;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            if (force > 0f) rb.AddExplosionForce(force * rb.mass, centre, radius, 0.6f, ForceMode.Impulse);
            bodies[i] = rb;
            activeDebris++;
        }

        private void LateUpdate()
        {
            if (activeDebris == 0 || meshRenderer == null) return;
            float now = Time.time;
            // Keep the one skinned mesh's bounds around its flying bones (no updateWhenOffscreen cost).
            Bounds b = restBounds;
            var root = meshRenderer.transform;
            for (int i = 0; i < fragments.Length; i++)
            {
                var rb = bodies[i];
                if (rb == null) continue;
                b.Encapsulate(root.InverseTransformPoint(fragments[i].position));
                float age = now - detachTime[i];
                // Settled (or taking too long): freeze into static rubble.
                if ((age > settleSeconds && rb.IsSleeping()) || age > maxDebrisSeconds)
                {
                    Destroy(rb);
                    bodies[i] = null;
                    activeDebris--;
                }
            }
            b.Expand(1f);
            meshRenderer.localBounds = b;
        }

        /// <summary>Put every fragment back (the range reset).</summary>
        public void Rebuild()
        {
            for (int i = 0; i < fragments.Length; i++)
            {
                if (bodies[i] != null)
                {
                    bodies[i].isKinematic = true; // stop it this frame; Destroy lands at frame end
                    Destroy(bodies[i]);
                    bodies[i] = null;
                }
                detached[i] = false;
                fragments[i].SetLocalPositionAndRotation(restPos[i], restRot[i]);
            }
            DetachedCount = 0;
            activeDebris = 0;
            if (meshRenderer != null) meshRenderer.localBounds = restBounds;
        }

        public static void RebuildAll()
        {
            for (int i = 0; i < all.Count; i++) all[i].Rebuild();
        }
    }
}
