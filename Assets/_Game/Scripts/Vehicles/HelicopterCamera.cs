using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Vehicles
{
    /// <summary>
    /// Chase camera for the helicopter, on the main camera next to the
    /// ThirdPersonCamera (which it switches off while flying). It trails the
    /// helicopter's heading; mouse Y / right stick Y tilt it to aim, and the
    /// screen centre is where the guns and rockets go.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    [RequireComponent(typeof(Camera))]
    public class HelicopterCamera : MonoBehaviour
    {
        [SerializeField] private float distance = 13f;
        [SerializeField] private float height = 3.2f;
        [SerializeField] private float lookAhead = 30f;
        [SerializeField] private float headingFollow = 3.5f; // lags the nose a little in turns, so you feel the swing
        [SerializeField] private float positionFollow = 5f; // trails the helicopter: you see it gather and shed speed
        [SerializeField] private float fieldOfView = 68f;
        [Tooltip("Extra field of view at speed (degrees at 30 m/s): a sense of rush.")]
        [SerializeField] private float speedFov = 6f;
        [Header("Aim (degrees per mouse pixel; per second for the stick)")]
        [SerializeField] private float mouseSensitivity = 0.1f;
        [SerializeField] private float stickSpeed = 70f;
        [SerializeField] private float minPitch = -15f;
        [SerializeField] private float maxPitch = 45f;
        [SerializeField] private bool invertY;
        [SerializeField] private float collisionRadius = 0.4f;
        [SerializeField] private LayerMask collisionMask = ~0;

        private Camera cam;
        private ThirdPersonCamera onFoot;
        private HelicopterPilot pilot;
        private float yaw;
        private float defaultFov;

        public bool Engaged => pilot != null;
        public float Pitch { get; set; } = 8f;
        public Ray AimRay => new Ray(transform.position, transform.forward);

        private void Awake()
        {
            cam = GetComponent<Camera>();
            onFoot = GetComponent<ThirdPersonCamera>();
            enabled = false;
        }

        public void Engage(HelicopterPilot p)
        {
            pilot = p;
            yaw = p.transform.eulerAngles.y;
            Pitch = 8f;
            if (onFoot != null) onFoot.enabled = false;
            defaultFov = cam.fieldOfView;
            enabled = true;
            Place(0f, snap: true);
        }

        public void Release()
        {
            if (pilot == null) return;
            var heli = pilot.transform;
            pilot = null;
            enabled = false;
            cam.fieldOfView = defaultFov;
            if (onFoot != null)
            {
                onFoot.enabled = true;
                onFoot.Yaw = heli.eulerAngles.y;
                onFoot.Pitch = 10f;
                onFoot.Snap();
            }
        }

        private void Update()
        {
            if (pilot == null || pilot.Input == null) return;
            var input = pilot.Input;
            float dy = input.Look.y * mouseSensitivity + input.LookStick.y * stickSpeed * Time.deltaTime;
            Pitch = Mathf.Clamp(Pitch + dy * (invertY ? 1f : -1f), minPitch, maxPitch);
        }

        private void LateUpdate()
        {
            if (pilot == null) return;
            Place(Time.deltaTime, snap: false);
        }

        private void Place(float dt, bool snap)
        {
            var heli = pilot.transform;
            float wantYaw = heli.eulerAngles.y;
            yaw = snap ? wantYaw : Mathf.LerpAngle(yaw, wantYaw, 1f - Mathf.Exp(-headingFollow * dt));
            Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
            Vector3 pivot = heli.position + Vector3.up * height;
            Quaternion look = heading * Quaternion.Euler(Pitch, 0f, 0f);
            Vector3 back = look * Vector3.back;
            float allowed = distance;
            if (Physics.SphereCast(pivot, collisionRadius, back, out RaycastHit hit, distance,
                    collisionMask & ~(1 << heli.gameObject.layer), QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(2f, hit.distance);
            Vector3 want = pivot + back * allowed;
            transform.position = snap ? want : Vector3.Lerp(transform.position, want, 1f - Mathf.Exp(-positionFollow * dt));
            // Aim through a point ahead of the helicopter along the tilted view.
            Vector3 aim = pivot + look * Vector3.forward * lookAhead;
            transform.rotation = Quaternion.LookRotation(aim - transform.position);
            cam.fieldOfView = fieldOfView + speedFov * Mathf.Clamp01(pilot.Speed / 30f);
        }
    }
}
