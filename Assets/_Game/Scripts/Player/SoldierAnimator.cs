using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Drives standing and crouching locomotion from the motor's actual velocity.
    /// </summary>
    public class SoldierAnimator : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private SoldierMotor motor;
        [Tooltip("Damping on the blend-tree parameters (seconds).")]
        [SerializeField] private float moveDampTime = 0.08f;

        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int Speed = Animator.StringToHash("Speed");
        private static readonly int Aiming = Animator.StringToHash("Aiming");
        private static readonly int Grounded = Animator.StringToHash("Grounded");
        private static readonly int Crouching = Animator.StringToHash("Crouching");

        public Animator Animator => animator;

        private void Awake()
        {
            if (motor == null) motor = GetComponent<SoldierMotor>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = false;
        }

        private void Update()
        {
            if (animator == null || motor == null) return;
            float dt = Time.deltaTime;
            Vector3 local = transform.InverseTransformDirection(motor.AnimationVelocity);

            animator.SetFloat(MoveX, local.x, moveDampTime, dt);
            animator.SetFloat(MoveZ, local.z, moveDampTime, dt);
            animator.SetFloat(Speed, motor.AnimationVelocity.magnitude);
            animator.SetBool(Aiming, motor.FacingCamera);
            animator.SetBool(Grounded, motor.IsGrounded);
            animator.SetBool(Crouching, motor.IsCrouching);
        }
    }
}
