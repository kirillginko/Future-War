using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Trauma-style shake added on top of wherever the camera rig put the
    /// camera this frame. Runs after the rig, and the rig places the camera
    /// from scratch every frame, so offsets never accumulate.
    /// </summary>
    [DefaultExecutionOrder(1100)]
    public class CameraShake : MonoBehaviour
    {
        [SerializeField] private float maxOffset = 0.18f;
        [SerializeField] private float maxRoll = 2.5f;
        [SerializeField] private float decay = 1.6f;
        [SerializeField] private float frequency = 22f;

        private float trauma;
        private float seed;

        public float Trauma => trauma;

        private void Awake() => seed = Random.value * 100f;

        /// <summary>0..1; stacks up to 1.</summary>
        public void Add(float amount) => trauma = Mathf.Clamp01(trauma + amount);

        private void LateUpdate()
        {
            if (trauma <= 0f) return;
            float k = trauma * trauma;
            float t = Time.time * frequency;
            Vector3 offset = new Vector3(Mathf.PerlinNoise(seed, t) - 0.5f, Mathf.PerlinNoise(seed + 1f, t) - 0.5f, 0f) * (2f * maxOffset * k);
            float roll = (Mathf.PerlinNoise(seed + 2f, t) - 0.5f) * 2f * maxRoll * k;
            transform.position += transform.rotation * offset;
            transform.rotation *= Quaternion.Euler(0f, 0f, roll);
            trauma = Mathf.Max(0f, trauma - decay * Time.deltaTime);
        }
    }
}
