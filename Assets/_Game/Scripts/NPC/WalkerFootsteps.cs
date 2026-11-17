using UnityEngine;

namespace ForestSoldier.NPC
{
    /// <summary>
    /// Receives the Walking clip's Footstep events on a walker's model and plays
    /// a quiet 3D grass step, so you can hear a patrol nearby. Silent once dead
    /// or standing still.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class WalkerFootsteps : MonoBehaviour
    {
        [SerializeField] private AudioClip[] steps = System.Array.Empty<AudioClip>();
        [Range(0f, 1f)] [SerializeField] private float volume = 0.45f;

        private AudioSource source;
        private WalkerNPC walker;
        private int last = -1;
        private float nextStep;

        private void Awake()
        {
            source = GetComponent<AudioSource>();
            walker = GetComponentInParent<WalkerNPC>();
        }

        // Animation events go to components on the Animator's object.
        public void Footstep(AnimationEvent contact)
        {
            if (!isActiveAndEnabled || walker == null || !walker.IsAlive || walker.CurrentSpeed < 0.2f ||
                steps.Length == 0 || Time.time < nextStep || contact.animatorClipInfo.weight < 0.3f) return;
            int i = Random.Range(0, steps.Length);
            if (steps.Length > 1 && i == last) i = (i + 1) % steps.Length;
            last = i;
            source.pitch = Random.Range(0.94f, 1.04f);
            source.PlayOneShot(steps[i], volume);
            nextStep = Time.time + 0.12f;
        }
    }
}
