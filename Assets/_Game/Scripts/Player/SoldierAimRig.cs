using ForestSoldier.Combat;
using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// After the Animator (and the reload overlay), turns the whole upper body
    /// about the chest, seats the rifle's sights at the right eye, then applies recoil.
    ///
    /// The rig parents the hands, arm poles and SMG to the root bone, not the
    /// arms, so "the upper body" is several roots: the chest (with arms and head
    /// under it) plus those root-level bones. Rotating them together about one
    /// pivot keeps the hands on the gun.
    /// </summary>
    public class SoldierAimRig : MonoBehaviour
    {
        [SerializeField] private SoldierMotor motor;
        [SerializeField] private HitscanWeapon weapon;
        [SerializeField] private UpperBodyOverlay overlay;
        [Tooltip("Pivot of the rotation (Spineupper).")]
        [SerializeField] private Transform chest;
        [Tooltip("Spineupper plus every upper-body bone parented to the root.")]
        [SerializeField] private Transform[] upperRoots;
        [SerializeField] private Transform muzzle;

        [SerializeField] private float blendSpeed = 7f;
        [SerializeField] private float maxYawCorrection = 50f;
        [SerializeField] private float maxPitchCorrection = 70f;
        [Tooltip("How fast gun recoil settles (1/s).")]
        [SerializeField] private float recoilReturn = 14f;
        [SerializeField] private float maxRecoilPitch = 10f;

        private float weight;
        private float recoilPitch, recoilYaw;
        private Transform head, rifle;
        private (Transform shoulder, Transform elbow, Transform wrist) leftArm, rightArm;
        // The sight rail is above the bore. The head pivot is near the base of the goggles.
        private const float SightHeight = 0.075f;
        private static readonly Vector3 EyeOffset = new(0.035f, 0.08f, 0.07f);
        public Vector3 EyePosition => head != null ? head.position + transform.rotation * EyeOffset
            : transform.position + Vector3.up * 1.65f;
        public Vector3 SightPosition => muzzle.position + muzzle.up * SightHeight;

        public float Weight => weight;

        public void AddRecoil(float pitchDegrees, float yawDegrees)
        {
            recoilPitch = Mathf.Min(maxRecoilPitch, recoilPitch + pitchDegrees);
            recoilYaw += yawDegrees;
        }

        private void Awake()
        {
            if (motor == null) motor = GetComponent<SoldierMotor>();
            if (weapon == null) weapon = GetComponent<HitscanWeapon>();
            if (overlay == null) overlay = GetComponent<UpperBodyOverlay>();
            if (chest == null || upperRoots == null) return;
            var bones = chest.GetComponentsInChildren<Transform>();
            head = System.Array.Find(bones, bone => bone.name == "head");
            rifle = System.Array.Find(upperRoots, bone => bone != null && bone.name == "SMGBone");
            leftArm = FindArm(".L");
            rightArm = FindArm(".R");

            (Transform, Transform, Transform) FindArm(string side) =>
                (System.Array.Find(bones, bone => bone.name == "ArmUpper" + side),
                 System.Array.Find(bones, bone => bone.name == "ArmLower" + side),
                 System.Array.Find(upperRoots, bone => bone != null && bone.name == "HandWrist" + side));
        }

        public void ApplyAim()
        {
            float dt = Time.deltaTime;
            float overlayWeight = overlay != null ? overlay.Weight : 0f;
            float target = motor != null && motor.FacingCamera ? 1f - overlayWeight : 0f;
            weight = weapon != null && weapon.TriggerHeld ? target : Mathf.MoveTowards(weight, target, blendSpeed * dt);

            float decay = Mathf.Exp(-recoilReturn * dt);
            recoilPitch *= decay;
            recoilYaw *= decay;

            if (chest == null || muzzle == null || upperRoots == null) return;
            Vector3 pivot = chest.position;

            // Turning the torso moves the muzzle, which changes the direction to
            // a near aim point; a few passes converge (one is plenty past ~10 m).
            Quaternion total = Quaternion.identity;
            if (weight > 1e-4f && weapon != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    Quaternion step = AimStep();
                    if (Quaternion.Angle(step, Quaternion.identity) < 0.05f) break;
                    Rotate(step, pivot);
                    total = step * total;
                }
            }
            // Blend: keep `weight` of the full correction.
            Quaternion blendFix = Quaternion.Slerp(Quaternion.identity, total, weight) * Quaternion.Inverse(total);
            Rotate(blendFix, pivot);

            AlignRifle(1f - overlayWeight);

            if (recoilPitch < 0.01f && Mathf.Abs(recoilYaw) < 0.01f) return;
            Quaternion recoil = Quaternion.AngleAxis(recoilYaw, Vector3.up) *
                                Quaternion.AngleAxis(-recoilPitch, muzzle.right);
            Rotate(recoil, head != null ? EyePosition : pivot);
        }

        private void AlignRifle(float poseWeight)
        {
            if (poseWeight <= 1e-4f || head == null || rifle == null || leftArm.wrist == null || rightArm.wrist == null ||
                leftArm.shoulder == null || leftArm.elbow == null || rightArm.shoulder == null || rightArm.elbow == null)
                return;

            Vector3 start = muzzle.position;
            Vector3 forward = muzzle.forward;
            if (Vector3.ProjectOnPlane(forward, Vector3.up).sqrMagnitude < 0.01f) return;
            Quaternion desiredRotation = Quaternion.LookRotation(forward, Vector3.up);
            Vector3 desiredPosition = start;
            if (weight > 1e-4f && weapon != null && (weapon.AimPoint - EyePosition).sqrMagnitude > 0.25f)
            {
                Vector3 eye = EyePosition;
                float depth = Mathf.Clamp(Vector3.Dot(start - eye, forward), 0.25f, 1.2f);
                Vector3 toTarget = weapon.AimPoint - eye;
                // The crosshair is on the bore line. Offset its origin below
                // the eye so the physical sights sit on the eye line.
                Vector3 bore = toTarget + desiredRotation * Vector3.up * SightHeight;
                if (Vector3.ProjectOnPlane(bore.normalized, Vector3.up).sqrMagnitude > 0.01f)
                {
                    desiredRotation = Quaternion.LookRotation(Vector3.Slerp(forward, bore.normalized, weight), Vector3.up);
                    Vector3 sightAligned = eye + desiredRotation * new Vector3(0f, -SightHeight, depth);
                    desiredPosition = Vector3.Lerp(start, sightAligned, weight);
                }
            }

            // The hands and rifle are siblings in this rig. Move them together
            // and refit both elbows so the grip stays fixed on the weapon.
            Quaternion turn = desiredRotation * Quaternion.Inverse(muzzle.rotation);
            for (int attempt = 0; attempt < 6; attempt++, poseWeight *= 0.5f)
            {
                Quaternion rotation = Quaternion.Slerp(Quaternion.identity, turn, poseWeight);
                Vector3 position = Vector3.Lerp(start, desiredPosition, poseWeight);
                Vector3 left = position + rotation * (leftArm.wrist.position - start);
                Vector3 right = position + rotation * (rightArm.wrist.position - start);
                if (!CanReach(leftArm, left) || !CanReach(rightArm, right)) continue;

                rifle.SetPositionAndRotation(position + rotation * (rifle.position - start), rotation * rifle.rotation);
                PlaceArm(leftArm, left, rotation);
                PlaceArm(rightArm, right, rotation);
                return;
            }
        }

        private static bool CanReach((Transform shoulder, Transform elbow, Transform wrist) arm, Vector3 target)
        {
            float upperLength = Vector3.Distance(arm.shoulder.position, arm.elbow.position);
            float lowerLength = Vector3.Distance(arm.elbow.position, arm.wrist.position);
            float distance = Vector3.Distance(arm.shoulder.position, target);
            return distance > Mathf.Abs(upperLength - lowerLength) + 0.001f && distance < upperLength + lowerLength;
        }

        private static void PlaceArm((Transform shoulder, Transform elbow, Transform wrist) arm, Vector3 target, Quaternion gripTurn)
        {
            Vector3 shoulder = arm.shoulder.position;
            Vector3 elbow = arm.elbow.position;
            Vector3 wrist = arm.wrist.position;
            float upperLength = Vector3.Distance(shoulder, elbow);
            float lowerLength = Vector3.Distance(elbow, wrist);
            float distance = Vector3.Distance(shoulder, target);
            Vector3 direction = (target - shoulder).normalized;
            Vector3 bend = Vector3.ProjectOnPlane(elbow - shoulder, direction).normalized;
            float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2f * distance);
            float away = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            Vector3 nextElbow = shoulder + direction * along + bend * away;

            Quaternion upperTurn = Quaternion.FromToRotation(elbow - shoulder, nextElbow - shoulder);
            Vector3 forearm = upperTurn * (wrist - elbow);
            arm.shoulder.rotation = upperTurn * arm.shoulder.rotation;
            arm.elbow.rotation = Quaternion.FromToRotation(forearm, target - nextElbow) * arm.elbow.rotation;
            arm.wrist.SetPositionAndRotation(target, gripTurn * arm.wrist.rotation);
        }

        /// <summary>Yaw about world up, then pitch in the vertical plane (no roll) to put the barrel on the aim point.</summary>
        private Quaternion AimStep()
        {
            Vector3 desired = weapon.AimPoint - muzzle.position;
            if (desired.sqrMagnitude < 0.25f) return Quaternion.identity;
            desired.Normalize();
            Vector3 current = muzzle.forward;
            Vector3 cFlat = Vector3.ProjectOnPlane(current, Vector3.up);
            Vector3 dFlat = Vector3.ProjectOnPlane(desired, Vector3.up);
            if (cFlat.sqrMagnitude < 1e-4f || dFlat.sqrMagnitude < 1e-4f) return Quaternion.identity;
            float yaw = Mathf.Clamp(Vector3.SignedAngle(cFlat, dFlat, Vector3.up), -maxYawCorrection, maxYawCorrection);
            Quaternion qYaw = Quaternion.AngleAxis(yaw, Vector3.up);

            Vector3 c2 = qYaw * current;
            Vector3 c2Flat = Vector3.ProjectOnPlane(c2, Vector3.up).normalized;
            Vector3 axis = Vector3.Cross(c2Flat, Vector3.up); // horizontal, across the barrel
            float pitchNow = Vector3.SignedAngle(c2Flat, c2, axis);
            dFlat.Normalize();
            float pitchWanted = Vector3.SignedAngle(dFlat, desired, Vector3.Cross(dFlat, Vector3.up));
            float pitch = Mathf.Clamp(pitchWanted - pitchNow, -maxPitchCorrection, maxPitchCorrection);
            return Quaternion.AngleAxis(pitch, axis) * qYaw;
        }

        private void Rotate(Quaternion q, Vector3 pivot)
        {
            foreach (var b in upperRoots)
            {
                if (b == null) continue;
                b.SetPositionAndRotation(pivot + q * (b.position - pivot), q * b.rotation);
            }
        }
    }
}
