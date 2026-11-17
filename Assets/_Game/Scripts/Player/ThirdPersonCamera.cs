using UnityEngine;
using UnityEngine.Rendering;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Shoulder, front and first-person views with a shared look direction.
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
        [Header("Alternate views")]
        [SerializeField] private float frontDistance = 2.8f;
        [SerializeField] private float firstPersonFieldOfView = 72f;
        [SerializeField] private float firstPersonAimFieldOfView = 50f;
        [SerializeField] private float firstPersonNearClip = 0.025f;

        [Header("Look (degrees per mouse pixel)")]
        [SerializeField] private float sensitivity = 0.12f;
        [SerializeField] private float aimSensitivityMultiplier = 0.6f;
        [SerializeField] private bool invertY;
        [Header("Look (gamepad right stick)")]
        [SerializeField] private float stickYawSpeed = 200f;
        [SerializeField] private float stickPitchSpeed = 140f;
        [Tooltip("Stick deflection is raised to this power: small movements are finer.")]
        [SerializeField] private float stickResponseExponent = 1.8f;
        [Tooltip("Extra turn speed after holding the stick fully over for a moment.")]
        [SerializeField] private float stickTurnBoost = 0.6f;
        [SerializeField] private float stickTurnBoostDelay = 0.25f;
        [SerializeField] private float stickTurnBoostRamp = 0.35f;
        [Tooltip("Stick look slows to this while the crosshair is over a live target.")]
        [Range(0.2f, 1f)] [SerializeField] private float stickAimFriction = 0.55f;
        [SerializeField] private float aimFrictionRange = 120f;

        [Header("Pitch limits")]
        [SerializeField] private float minPitch = -60f;
        [SerializeField] private float maxPitch = 70f;

        [Header("Collision")]
        [SerializeField] private LayerMask collisionMask = ~0;
        [SerializeField] private float collisionRadius = 0.2f;
        [SerializeField] private float minDistance = 0.4f;
        [Tooltip("How fast the camera eases back out after an obstruction clears (m/s).")]
        [SerializeField] private float recoverSpeed = 6f;

        private Camera cam;
        private SoldierMotor motor;
        private SoldierAimRig aimRig;
        private (Renderer renderer, ShadowCastingMode shadows)[] thirdPersonRenderers;
        private bool bodyHidden, firstPersonSelected;
        private float orbitNearClip;
        private Ray gameplayAimRay;
        private float aimBlend;
        private float crouchDrop;
        private float currentDistance;
        private float stickHeld;

        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public Camera Camera => cam;
        public Transform Target => target;

        public bool IsFrontView { get; private set; }
        public bool UsesFirstPersonAim => firstPersonSelected;
        public bool IsFirstPerson => firstPersonSelected && !IsFrontView;
        public bool ShowsIronSights => IsFirstPerson && input != null && input.Aim;

        /// <summary>The shooting ray stays forward when the camera switches to the front.</summary>
        public Ray AimRay => IsFrontView || IsFirstPerson ? gameplayAimRay : new Ray(transform.position, transform.forward);

        private void Awake()
        {
            cam = GetComponent<Camera>();
            if (target != null && input == null) input = target.GetComponent<SoldierInput>();
            orbitNearClip = cam.nearClipPlane;
            SetTarget(target, input);
        }

        private void Start()
        {
            if (target != null) Yaw = target.eulerAngles.y;
            currentDistance = distance;
            Snap();
        }

        public void SetTarget(Transform t, SoldierInput i)
        {
            SetBodyHidden(false);
            target = t;
            input = i;
            motor = t != null ? t.GetComponent<SoldierMotor>() : null;
            aimRig = t != null ? t.GetComponent<SoldierAimRig>() : null;
            var model = t != null ? t.GetComponent<SoldierAnimator>()?.Animator : null;
            var body = new System.Collections.Generic.List<(Renderer, ShadowCastingMode)>();
            if (model != null)
            {
                // Keep only the separate arms, hands and SMG visible at the eye camera.
                // The body, head and radio gear can cross the near plane during a sprint.
                foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>())
                    if (renderer.name != "Object_7" && renderer.name != "Object_13" && renderer.name != "Object_23")
                        body.Add((renderer, renderer.shadowCastingMode));
            }
            thirdPersonRenderers = body.ToArray();
        }

        private void OnDisable()
        {
            SetBodyHidden(false);
            if (cam != null) cam.nearClipPlane = orbitNearClip;
        }

        private void Update()
        {
            if (input == null) return;
            if (input.FrontViewRequested)
            {
                input.FrontViewRequested = false;
                IsFrontView = !IsFrontView;
            }
            if (input.FirstPersonRequested)
            {
                input.FirstPersonRequested = false;
                firstPersonSelected = IsFrontView || !firstPersonSelected;
                IsFrontView = false;
            }
            Vector2 look = input.Look;
            float aimMul = input.Aim ? aimSensitivityMultiplier : 1f;
            float s = sensitivity * aimMul;
            float yawDelta = look.x * s, pitchDelta = look.y * s;

            Vector2 stick = input.LookStick;
            float mag = stick.magnitude;
            if (mag > 1e-3f)
            {
                Vector2 curved = stick / mag * Mathf.Pow(Mathf.Min(mag, 1f), stickResponseExponent);
                stickHeld = Mathf.Abs(stick.x) > 0.95f ? stickHeld + Time.deltaTime : 0f;
                float boost = 1f + stickTurnBoost * Mathf.Clamp01((stickHeld - stickTurnBoostDelay) / stickTurnBoostRamp);
                float friction = OverLiveTarget() ? stickAimFriction : 1f;
                float k = aimMul * friction * Time.deltaTime;
                yawDelta += curved.x * stickYawSpeed * boost * k;
                pitchDelta += curved.y * stickPitchSpeed * k;
            }
            else stickHeld = 0f;

            Yaw = Mathf.Repeat(Yaw + yawDelta, 360f);
            Pitch = Mathf.Clamp(Pitch + pitchDelta * (invertY ? 1f : -1f), minPitch, maxPitch);
        }

        private void LateUpdate()
        {
            if (target == null) return;
            bool aiming = input != null && input.Aim;
            float step = aimBlendTime > 0f ? Time.deltaTime / aimBlendTime : 1f;
            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, step);
            crouchDrop = Mathf.MoveTowards(crouchDrop, motor != null ? motor.CrouchDrop : 0f, 4f * Time.deltaTime);
            Place(Time.deltaTime);
        }

        private bool OverLiveTarget()
        {
            if (!Physics.Raycast(AimRay, out RaycastHit hit, aimFrictionRange, collisionMask, QueryTriggerInteraction.Ignore))
                return false;
            var d = hit.collider.GetComponentInParent<ForestSoldier.Combat.IDamageable>();
            return d != null && d.IsAlive;
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
            Vector3 pivot = target.position + pivotOffset - Vector3.up * crouchDrop;
            SetBodyHidden(IsFirstPerson);
            cam.nearClipPlane = IsFirstPerson ? firstPersonNearClip : orbitNearClip;

            if (firstPersonSelected)
            {
                Vector3 eye = aimRig != null ? aimRig.EyePosition : pivot + Vector3.up * 0.15f;
                gameplayAimRay = new Ray(eye, rot * Vector3.forward);
                if (IsFirstPerson)
                {
                    transform.SetPositionAndRotation(eye, rot);
                    cam.fieldOfView = Mathf.Lerp(firstPersonFieldOfView, firstPersonAimFieldOfView, blend);
                    return;
                }
            }
            else
            {
                Vector3 shoulder = ShoulderPosition(pivot, rot, blend, dt);
                gameplayAimRay = new Ray(shoulder, rot * Vector3.forward);
                if (!IsFrontView)
                {
                    transform.SetPositionAndRotation(shoulder, rot);
                    cam.fieldOfView = Mathf.Lerp(fieldOfView, aimFieldOfView, blend);
                    return;
                }
            }

            Quaternion frontRotation = Quaternion.Euler(8f, target.eulerAngles.y + 180f, 0f);
            Vector3 front = frontRotation * Vector3.back;
            float allowed = frontDistance;
            if (Physics.SphereCast(pivot, collisionRadius, front, out RaycastHit obstruction, allowed,
                    collisionMask, QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(0.05f, obstruction.distance);
            transform.SetPositionAndRotation(pivot + front * allowed, frontRotation);
            cam.fieldOfView = fieldOfView;
        }

        /// <summary>Frame the irons after the weapon has posed this frame.</summary>
        public void PresentFirstPersonSight(Transform muzzle, bool reloading)
        {
            if (!isActiveAndEnabled || !ShowsIronSights || reloading || aimRig == null || muzzle == null) return;

            Vector3 eye = aimRig.EyePosition;
            Vector3 sight = aimRig.SightPosition;
            Vector3 alongRail = muzzle.forward;
            float depth = Mathf.Clamp(Vector3.Dot(sight - eye, alongRail), 0.25f, 1.2f);
            Vector3 onSightLine = sight - alongRail * depth;
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.Min(aimBlend, aimRig.Weight));
            transform.SetPositionAndRotation(Vector3.Lerp(gameplayAimRay.origin, onSightLine, blend),
                Quaternion.Slerp(Quaternion.LookRotation(gameplayAimRay.direction, Vector3.up), muzzle.rotation, blend));
        }

        private void SetBodyHidden(bool hidden)
        {
            if (bodyHidden == hidden) return;
            bodyHidden = hidden;
            if (thirdPersonRenderers == null) return;
            foreach (var (renderer, shadows) in thirdPersonRenderers)
                if (renderer != null) renderer.shadowCastingMode = hidden ? ShadowCastingMode.ShadowsOnly : shadows;
        }

        private Vector3 ShoulderPosition(Vector3 pivot, Quaternion rot, float blend, float dt)
        {
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

            return shoulder + back * currentDistance;
        }
    }
}
