using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Over-the-shoulder orbit camera with mouse look, an aim zoom and
    /// sphere-cast collision so it does not clip through trees and walls.
    /// Put it on the camera GameObject (not parented to the player).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Camera))]
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform target;
        [SerializeField] private SoldierInput input;

        [Header("Framing")]
        [Tooltip("Orbit pivot above the target's origin (roughly neck height).")]
        [SerializeField] private Vector3 pivotOffset = new Vector3(0f, 1.5f, 0f);
        [SerializeField] private Vector3 shoulderOffset = new Vector3(0.55f, 0.15f, 0f);
        [SerializeField] private Vector3 aimShoulderOffset = new Vector3(0.75f, 0.12f, 0f);
        [SerializeField] private float distance = 3.4f;
        [SerializeField] private float aimDistance = 2.0f;
        [SerializeField] private float fieldOfView = 60f;
        [SerializeField] private float aimFieldOfView = 45f;
        [Tooltip("Seconds to blend between hip and aim framing.")]
        [SerializeField] private float aimBlendTime = 0.12f;

        [Header("Look (degrees per mouse pixel)")]
        [SerializeField] private float sensitivity = 0.12f;
        [SerializeField] private float aimSensitivityMultiplier = 0.6f;
        [SerializeField] private bool invertY;
        [SerializeField] private float minPitch = -60f;
        [SerializeField] private float maxPitch = 70f;

        [Header("Collision")]
        [SerializeField] private LayerMask collisionMask = ~0;
        [SerializeField] private float collisionRadius = 0.2f;
        [SerializeField] private float minDistance = 0.4f;
        [Tooltip("How fast the camera eases back out after an obstruction clears (m/s).")]
        [SerializeField] private float recoverSpeed = 6f;

        private Camera cam;
        private float aimBlend;
        private float currentDistance;

        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public Camera Camera => cam;
        public Transform Target => target;

        /// <summary>Ray through the screen centre; weapons aim along this.</summary>
        public Ray AimRay => new Ray(transform.position, transform.forward);

        private void Awake()
        {
            cam = GetComponent<Camera>();
            if (target != null && input == null) input = target.GetComponent<SoldierInput>();
        }

        private void Start()
        {
            if (target != null) Yaw = target.eulerAngles.y;
            currentDistance = distance;
            Snap();
        }

        public void SetTarget(Transform t, SoldierInput i)
        {
            target = t;
            input = i;
        }

        private void Update()
        {
            if (input == null) return;
            Vector2 look = input.Look;
            float s = sensitivity * (input.Aim ? aimSensitivityMultiplier : 1f);
            Yaw = Mathf.Repeat(Yaw + look.x * s, 360f);
            Pitch = Mathf.Clamp(Pitch + look.y * s * (invertY ? 1f : -1f), minPitch, maxPitch);
        }

        private void LateUpdate()
        {
            if (target == null) return;
            bool aiming = input != null && input.Aim;
            float step = aimBlendTime > 0f ? Time.deltaTime / aimBlendTime : 1f;
            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, step);
            Place(Time.deltaTime);
        }

        /// <summary>Kick the view up by some degrees (weapon recoil); the player pulls it back down.</summary>
        public void AddRecoil(float pitchDegrees, float yawDegrees = 0f)
        {
            Pitch = Mathf.Clamp(Pitch - pitchDegrees, minPitch, maxPitch);
            Yaw = Mathf.Repeat(Yaw + yawDegrees, 360f);
        }

        /// <summary>Jump straight to the framed position (no easing).</summary>
        public void Snap()
        {
            if (target == null) return;
            currentDistance = float.MaxValue;
            Place(0f);
        }

        private void Place(float dt)
        {
            float blend = Mathf.SmoothStep(0f, 1f, aimBlend);
            Quaternion rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 pivot = target.position + pivotOffset;

            // Shoulder offset first, so a wall beside the player pulls it in.
            Vector3 shoulderWanted = pivot + rot * Vector3.Lerp(shoulderOffset, aimShoulderOffset, blend);
            Vector3 toShoulder = shoulderWanted - pivot;
            Vector3 shoulder = shoulderWanted;
            if (toShoulder.sqrMagnitude > 1e-6f &&
                Physics.SphereCast(pivot, collisionRadius, toShoulder.normalized, out RaycastHit sideHit,
                    toShoulder.magnitude, collisionMask, QueryTriggerInteraction.Ignore))
                shoulder = pivot + toShoulder.normalized * Mathf.Max(0f, sideHit.distance);

            float wanted = Mathf.Lerp(distance, aimDistance, blend);
            float allowed = wanted;
            Vector3 back = rot * Vector3.back;
            if (Physics.SphereCast(shoulder, collisionRadius, back, out RaycastHit hit, wanted,
                    collisionMask, QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(minDistance, hit.distance);

            // Pull in instantly; ease back out.
            currentDistance = allowed < currentDistance || dt <= 0f
                ? allowed
                : Mathf.MoveTowards(currentDistance, allowed, recoverSpeed * dt);

            transform.SetPositionAndRotation(shoulder + back * currentDistance, rot);
            if (cam != null) cam.fieldOfView = Mathf.Lerp(fieldOfView, aimFieldOfView, blend);
        }
    }
}
