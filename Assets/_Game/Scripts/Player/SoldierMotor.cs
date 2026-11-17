using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// CharacterController movement: camera-relative WASD, sprint, gravity.
    /// Faces the movement direction normally and the camera's yaw while aiming.
    /// Animations are in-place; this component owns all translation.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-10)]
    public class SoldierMotor : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SoldierInput input;
        [SerializeField] private ThirdPersonCamera cameraRig;

        [Header("Speeds (m/s)")]
        public const float WalkSpeed = 0.99f;
        [SerializeField] private float sprintSpeed = 5.5f;
        [SerializeField] private float aimMoveSpeed = 1.8f;

        [Header("Analog stick pace")]
        [Tooltip("Stick deflection that reaches walking speed.")]
        [Range(0.2f, 0.8f)] [SerializeField] private float stickWalkAt = 0.5f;
        [Tooltip("Stick deflection that reaches the rifle run (the run clip's pace).")]
        [Range(0.5f, 0.95f)] [SerializeField] private float stickRunAt = 0.78f;
        [SerializeField] private float stickRunSpeed = 1.8f;
        [Tooltip("At full tilt the stick runs flat out (sprint speed).")]
        [SerializeField] private bool fullTiltSprints = true;
        public const float CrouchMoveSpeed = 1.22f;
        [SerializeField] private float crouchHeight = 1.35f;
        [Tooltip("Speed multiplier when moving backwards while aiming.")]
        [Range(0.3f, 1f)] [SerializeField] private float backwardMultiplier = 0.7f;
        [SerializeField] private float acceleration = 14f;
        [SerializeField] private float deceleration = 18f;

        [Header("Turning (deg/s)")]
        [SerializeField] private float turnSpeed = 720f;
        [SerializeField] private float aimTurnSpeed = 1440f;

        [Header("Gravity")]
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float maxFallSpeed = 40f;
        [Tooltip("Downward speed held while grounded so the controller hugs slopes.")]
        [SerializeField] private float groundStickSpeed = 4f;
        [SerializeField] private float groundProbeDistance = 0.25f;
        [SerializeField] private LayerMask groundMask = ~0;

        private CharacterController controller;
        private Vector3 planarVelocity;
        private float verticalSpeed;
        private float standingHeight;
        private Vector3 standingCenter;

        /// <summary>Horizontal velocity the controller actually achieved last frame (walls included).</summary>
        public Vector3 AnimationVelocity { get; private set; }
        public bool IsGrounded { get; private set; }
        /// <summary>Right mouse held: camera zooms, soldier faces the camera.</summary>
        public bool IsAiming { get; private set; }
        /// <summary>Aiming or firing: the soldier faces the camera's yaw and strafes.</summary>
        public bool FacingCamera { get; private set; }
        /// <summary>Degrees between the body's yaw and the camera's yaw.</summary>
        public float FacingError { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        public float CrouchDrop => IsCrouching ? standingHeight - controller.height : 0f;
        public float SprintSpeed => sprintSpeed;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            standingHeight = controller.height;
            standingCenter = controller.center;
            if (input == null) input = GetComponent<SoldierInput>();
            if (cameraRig == null) cameraRig = FindFirstObjectByType<ThirdPersonCamera>();
        }

        public void SetCamera(ThirdPersonCamera rig) => cameraRig = rig;

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);
            bool moving = move.sqrMagnitude > 0.0001f;
            float viewYaw = cameraRig != null ? cameraRig.Yaw : transform.eulerAngles.y;
            Vector3 wishDir = Quaternion.Euler(0f, viewYaw, 0f) * new Vector3(move.x, 0f, move.y);

            IsAiming = input.Aim;
            FacingCamera = IsAiming || input.Fire;
            UpdateStance(input.Crouch);
            float stickPace = input.MoveIsAnalog && !FacingCamera && !IsCrouching ? StickPace(move.magnitude) : -1f;
            IsSprinting = stickPace >= 0f
                ? stickPace > stickRunSpeed + 0.5f
                : input.Sprint && !FacingCamera && !IsCrouching && moving;

            float speed;
            if (FacingCamera)
            {
                float back = moving ? Mathf.Clamp01(-move.y / move.magnitude) : 0f;
                speed = aimMoveSpeed * Mathf.Lerp(1f, backwardMultiplier, back);
            }
            else if (stickPace >= 0f) speed = moving ? stickPace / move.magnitude : 0f; // wishDir is already scaled by the stick
            else speed = IsSprinting ? sprintSpeed : WalkSpeed;
            if (IsCrouching) speed = CrouchMoveSpeed;

            Vector3 target = wishDir * speed;
            float rate = target.sqrMagnitude >= planarVelocity.sqrMagnitude ? acceleration : deceleration;
            planarVelocity = Vector3.MoveTowards(planarVelocity, target, rate * speed.AtLeast(1f) * dt);

            // Facing
            if (FacingCamera || (cameraRig != null && cameraRig.UsesFirstPersonAim)) TurnTowards(viewYaw, aimTurnSpeed, dt);
            else if (moving) TurnTowards(Mathf.Atan2(wishDir.x, wishDir.z) * Mathf.Rad2Deg, turnSpeed, dt);

            // Gravity
            if (controller.isGrounded && verticalSpeed <= 0f) verticalSpeed = -groundStickSpeed;
            else verticalSpeed = Mathf.Max(verticalSpeed + gravity * dt, -maxFallSpeed);

            Vector3 before = transform.position;
            controller.Move((planarVelocity + Vector3.up * verticalSpeed) * dt);
            Vector3 moved = (transform.position - before) / dt;
            moved.y = 0f;
            AnimationVelocity = moved;

            // Running into a wall should not keep building speed we can't use.
            if (moved.sqrMagnitude < planarVelocity.sqrMagnitude)
                planarVelocity = Vector3.MoveTowards(planarVelocity, moved, deceleration * dt);

            IsGrounded = controller.isGrounded || ProbeGround();
            FacingError = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, viewYaw));
        }

        private void UpdateStance(bool crouch)
        {
            if (crouch == IsCrouching) return;
            if (!crouch)
            {
                Vector3 top = transform.TransformPoint(controller.center + Vector3.up *
                    (controller.height * 0.5f - controller.radius));
                int mask = ~(1 << gameObject.layer) & ~Physics.IgnoreRaycastLayer;
                if (Physics.SphereCast(top, controller.radius - controller.skinWidth, Vector3.up,
                        out _, CrouchDrop, mask, QueryTriggerInteraction.Ignore)) return;
            }

            float height = crouch ? Mathf.Clamp(crouchHeight, 2f * controller.radius, standingHeight) : standingHeight;
            controller.height = height;
            controller.center = standingCenter + Vector3.down * ((standingHeight - height) * 0.5f);
            IsCrouching = crouch;
        }

        /// <summary>Speed in m/s for a stick deflection: walk up to stickWalkAt, the rifle run by stickRunAt, then sprint at full tilt.</summary>
        public float StickPace(float m)
        {
            m = Mathf.Clamp01(m);
            if (m <= stickWalkAt) return WalkSpeed * (m / stickWalkAt);
            if (m <= stickRunAt) return Mathf.Lerp(WalkSpeed, stickRunSpeed, Mathf.InverseLerp(stickWalkAt, stickRunAt, m));
            float top = fullTiltSprints ? sprintSpeed : stickRunSpeed;
            return Mathf.Lerp(stickRunSpeed, top, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(stickRunAt, 0.98f, m)));
        }

        private void TurnTowards(float yaw, float degPerSec, float dt)
        {
            float current = transform.eulerAngles.y;
            float next = Mathf.MoveTowardsAngle(current, yaw, degPerSec * dt);
            transform.rotation = Quaternion.Euler(0f, next, 0f);
        }

        private bool ProbeGround()
        {
            float r = controller.radius * 0.9f;
            Vector3 origin = transform.position + controller.center + Vector3.down * (controller.height * 0.5f - controller.radius);
            return Physics.SphereCast(origin, r, Vector3.down, out _, groundProbeDistance + controller.skinWidth,
                groundMask, QueryTriggerInteraction.Ignore);
        }
    }

    internal static class FloatExt
    {
        public static float AtLeast(this float v, float min) => v < min ? min : v;
    }
}
