using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>Plays one grass sample at each planted foot in the locomotion clips.</summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SoldierFootsteps : MonoBehaviour
    {
        [SerializeField] private AudioClip[] walk = System.Array.Empty<AudioClip>();
        [SerializeField] private AudioClip[] run = System.Array.Empty<AudioClip>();

        private AudioSource source;
        private SoldierMotor motor;
        private int lastWalk = -1, lastRun = -1;
        private float nextStep;

        private void Awake()
        {
            source = GetComponent<AudioSource>();
            motor = GetComponentInParent<SoldierMotor>();
        }

        // Animation events are sent to the same object as the Animator.
        public void Footstep(AnimationEvent contact)
        {
            if (!isActiveAndEnabled || motor == null || !motor.IsGrounded ||
                motor.AnimationVelocity.sqrMagnitude < 0.04f || Time.time < nextStep ||
                contact.animatorClipInfo.weight < 0.3f) return;

            bool running = motor.IsSprinting || motor.AnimationVelocity.sqrMagnitude > 1.45f * 1.45f;
            AudioClip clip = running ? Choose(run, ref lastRun) : Choose(walk, ref lastWalk);
            if (clip == null) return;

            source.pitch = Random.Range(0.96f, 1.04f);
            source.PlayOneShot(clip, motor.IsCrouching ? 0.35f : 0.6f);
            nextStep = Time.time + 0.12f;
        }

        private static AudioClip Choose(AudioClip[] clips, ref int last)
        {
            if (clips.Length == 0) return null;
            if (clips.Length == 1) return clips[0];
            int index = Random.Range(0, last < 0 ? clips.Length : clips.Length - 1);
            if (last >= 0 && index >= last) index++;
            last = index;
            return clips[index];
        }
    }
}
