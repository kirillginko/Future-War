using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>
    /// One pooled explosion: its particle systems (core flash, fireball, shockwave
    /// ring, ground dust, sparks, glowing embers with trails, bouncing rocks, dirt,
    /// a rising smoke column), a light flash, a 3D boom, and a scorch mark left on
    /// the ground. Play() re-uses the same instance.
    /// </summary>
    public class ExplosionEffect : MonoBehaviour
    {
        [SerializeField] private ParticleSystem[] systems = System.Array.Empty<ParticleSystem>();
        [SerializeField] private Light flash;
        [SerializeField] private float flashIntensity = 18f;
        [SerializeField] private float flashTime = 0.25f;
        [SerializeField] private AudioSource source;
        [SerializeField] private AudioClip boom;
        [Header("Scorch marks")]
        [Tooltip("Quads laid on the ground under the blast, cycled; each fades out over its life.")]
        [SerializeField] private Renderer[] scorches = System.Array.Empty<Renderer>();
        [SerializeField] private Vector2 scorchSize = new Vector2(2.4f, 3.4f);
        [SerializeField] private float scorchLife = 45f;
        [SerializeField] private LayerMask groundMask = 1; // Default (terrain, structures)

        private float flashLeft;
        private int nextScorch;
        private float[] scorchAge;
        private MaterialPropertyBlock block;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public int ScorchesShown { get; private set; }

        public bool Playing => flashLeft > 0f || (systems.Length > 0 && systems[0].IsAlive(true));

        private void Awake()
        {
            if (flash != null) flash.intensity = 0f;
            block = new MaterialPropertyBlock();
            scorchAge = new float[scorches.Length];
            for (int i = 0; i < scorches.Length; i++)
            {
                scorches[i].transform.SetParent(null, true); // they stay where they're laid
                scorches[i].enabled = false;
                scorchAge[i] = float.MaxValue;
            }
        }

        public void Play(Vector3 at)
        {
            transform.position = at;
            foreach (var s in systems)
            {
                s.Clear(true);
                s.Play(true);
            }
            flashLeft = flashTime;
            LayScorch(at);
            if (source != null && boom != null)
            {
                source.pitch = Random.Range(0.92f, 1.05f);
                source.PlayOneShot(boom);
            }
        }

        private void LayScorch(Vector3 at)
        {
            if (scorches.Length == 0) return;
            if (!Physics.Raycast(at + Vector3.up * 0.6f, Vector3.down, out RaycastHit ground, 2.5f, groundMask, QueryTriggerInteraction.Ignore))
                return; // went off on a wall or in the air: no mark on the ground
            int i = nextScorch;
            nextScorch = (nextScorch + 1) % scorches.Length;
            var r = scorches[i];
            float size = Random.Range(scorchSize.x, scorchSize.y);
            r.transform.SetPositionAndRotation(ground.point + ground.normal * 0.015f,
                Quaternion.LookRotation(-ground.normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            r.transform.localScale = new Vector3(size, size, 1f);
            r.enabled = true;
            scorchAge[i] = 0f;
            ScorchesShown++;
            SetScorchAlpha(r, 1f);
        }

        private void SetScorchAlpha(Renderer r, float a)
        {
            r.GetPropertyBlock(block);
            var c = r.sharedMaterial.GetColor(BaseColorId);
            c.a *= a;
            block.SetColor(BaseColorId, c);
            r.SetPropertyBlock(block);
        }

        public void ClearScorches()
        {
            for (int i = 0; i < scorches.Length; i++)
            {
                scorches[i].enabled = false;
                scorchAge[i] = float.MaxValue;
            }
        }

        private void Update()
        {
            for (int i = 0; i < scorches.Length; i++)
            {
                if (scorchAge[i] >= scorchLife) continue;
                scorchAge[i] += Time.deltaTime;
                if (scorchAge[i] >= scorchLife) scorches[i].enabled = false;
                else if (scorchAge[i] > scorchLife * 0.7f)
                    SetScorchAlpha(scorches[i], 1f - (scorchAge[i] - scorchLife * 0.7f) / (scorchLife * 0.3f));
            }
            if (flashLeft <= 0f) return;
            flashLeft = Mathf.Max(0f, flashLeft - Time.deltaTime);
            if (flash != null) flash.intensity = flashIntensity * (flashLeft / flashTime) * (flashLeft / flashTime);
        }
    }
}
