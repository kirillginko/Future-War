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
        private Transform selectedSight, supportGrip, rearGrip, stock;
        private bool verticalSupport, preserveAnimatedGrip;
        private GrenadeLauncher launcher;
        private (Transform shoulder, Transform elbow, Transform wrist) leftArm, rightArm;
        private (Transform palm, Transform fingers, Transform thumb) leftHand, rightHand;
        // The sight rail is above the bore. The head pivot is near the base of the goggles.
        private const float SightHeight = 0.075f;
        private static readonly Vector3 EyeOffset = new(0.035f, 0.08f, 0.07f);
        public Vector3 EyePosition => head != null ? head.position + transform.rotation * EyeOffset
            : transform.position + Vector3.up * 1.65f;
        public Vector3 SightPosition => selectedSight != null ? selectedSight.position : muzzle.position + muzzle.up * SightHeight;

        public void SetWeaponSight(Transform sight, Transform support, Transform grip, Transform butt, bool vertical)
        {
            selectedSight = sight;
            supportGrip = support;
            rearGrip = grip;
            stock = butt;
            verticalSupport = vertical;
            preserveAnimatedGrip = grip != null && grip.name == "StockGrip";
            recoilPitch = recoilYaw = 0f;
        }

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
            launcher = GetComponent<GrenadeLauncher>();
            if (chest == null || upperRoots == null) return;
            var bones = chest.GetComponentsInChildren<Transform>();
            head = System.Array.Find(bones, bone => bone.name == "head");
            rifle = System.Array.Find(upperRoots, bone => bone != null && bone.name == "SMGBone");
            leftArm = FindArm(".L");
            rightArm = FindArm(".R");
            var allBones = transform.GetComponent<SoldierAnimator>().Animator.GetComponentsInChildren<Transform>();
            leftHand = FindHand(".L");
            rightHand = FindHand(".R");

            (Transform, Transform, Transform) FindArm(string side) =>
                (System.Array.Find(bones, bone => bone.name == "ArmUpper" + side),
                 System.Array.Find(bones, bone => bone.name == "ArmLower" + side),
                 System.Array.Find(upperRoots, bone => bone != null && bone.name == "HandWrist" + side));
            (Transform, Transform, Transform) FindHand(string side) =>
                (System.Array.Find(allBones, bone => bone.name == "Hand" + side + ".001"),
                 System.Array.Find(allBones, bone => bone.name == "Fingers" + side),
                 System.Array.Find(allBones, bone => bone.name == "ThumbBase" + side));
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
            if (rearGrip != null && !preserveAnimatedGrip && overlayWeight > 0f)
                rifle.position += (HandContact(rightHand) - rearGrip.position - muzzle.right * 0.025f) * overlayWeight;

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
            Vector3 sightOffset = Quaternion.Inverse(muzzle.rotation) * (SightPosition - start);
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
                Vector3 lateralSight = new(sightOffset.x, sightOffset.y, 0f);
                Vector3 bore = toTarget + desiredRotation * lateralSight;
                if (Vector3.ProjectOnPlane(bore.normalized, Vector3.up).sqrMagnitude > 0.01f)
                {
                    desiredRotation = Quaternion.LookRotation(Vector3.Slerp(forward, bore.normalized, weight), Vector3.up);
                    Vector3 sightAligned = eye + desiredRotation * new Vector3(-sightOffset.x, -sightOffset.y, depth);
                    desiredPosition = Vector3.Lerp(start, sightAligned, weight);
                }
            }
            if (stock != null && !preserveAnimatedGrip)
            {
                Vector3 stockOffset = Quaternion.Inverse(muzzle.rotation) * (stock.position - start);
                // Seat the butt at the padded shoulder in all three axes.
                Vector3 shoulder = rightArm.shoulder.position + transform.right * 0.08f
                    - transform.forward * 0.015f + Vector3.up * 0.025f;
                desiredPosition = shoulder - desiredRotation * stockOffset;
            }

            // The hands and rifle are siblings in this rig. Move them together
            // and refit both elbows so the grip stays fixed on the weapon.
            Quaternion turn = desiredRotation * Quaternion.Inverse(muzzle.rotation);
            for (int attempt = 0; attempt < 6; attempt++, poseWeight *= 0.5f)
            {
                Quaternion rotation = Quaternion.Slerp(Quaternion.identity, turn, poseWeight);
                Vector3 position = Vector3.Lerp(start, desiredPosition, poseWeight);
                Quaternion gunRotation = rotation * muzzle.rotation;
                Vector3 forwardAxis = gunRotation * Vector3.forward;
                Vector3 upAxis = gunRotation * Vector3.up;
                Vector3 rightAxis = gunRotation * Vector3.right;
                Quaternion leftTurn = HandTurn(leftHand, forwardAxis, verticalSupport ? rightAxis : -upAxis);
                Quaternion rightTurn = HandTurn(rightHand, forwardAxis, rightAxis);
                Vector3 leftContact = supportGrip != null ? supportGrip.position : HandContact(leftHand);
                Vector3 rightContact = rearGrip != null ? rearGrip.position : HandContact(rightHand);
                float supportDrop = launcher != null && launcher.Available ? launcher.SupportDrop : 0.025f;
                leftContact = position + rotation * (leftContact - start)
                    + (verticalSupport ? -rightAxis * 0.025f : -upAxis * supportDrop);
                rightContact = position + rotation * (rightContact - start) + rightAxis * 0.025f;
                Quaternion leftRotation = Quaternion.Slerp(Quaternion.identity, leftTurn, poseWeight);
                Quaternion rightRotation = preserveAnimatedGrip ? rotation
                    : Quaternion.Slerp(Quaternion.identity, rightTurn, poseWeight);
                Vector3 left = leftContact - leftRotation * (HandContact(leftHand) - leftArm.wrist.position);
                Vector3 right = preserveAnimatedGrip
                    ? position + rotation * (rightArm.wrist.position - start)
                    : rightContact - rightRotation * (HandContact(rightHand) - rightArm.wrist.position);
                if (!CanReach(leftArm, left) || !CanReach(rightArm, right)) continue;

                rifle.SetPositionAndRotation(position + rotation * (rifle.position - start), rotation * rifle.rotation);
                PlaceArm(leftArm, left, leftRotation);
                PlaceArm(rightArm, right, rightRotation);
                CurlFingers(leftHand.fingers, verticalSupport ? rightAxis : upAxis, poseWeight);
                if (!preserveAnimatedGrip) CurlFingers(rightHand.fingers, -rightAxis, poseWeight);
                return;
            }
        }

        public Vector3 SupportHandPosition => HandContact(leftHand);
        public Vector3 FiringHandPosition => HandContact(rightHand);

        private Vector3 HandContact((Transform palm, Transform fingers, Transform thumb) hand)
        {
            // Measured from the skinned glove vertices. The palm surface is offset
            // from the bone plane; the midpoint of the bones leaves the glove floating.
            Vector3 along = hand.fingers.position - hand.palm.position;
            Vector3 across = hand.thumb.position - hand.palm.position;
            bool left = hand.palm == leftHand.palm;
            Vector3 coordinates = left
                ? (verticalSupport ? new Vector3(0.515f, -0.333f, 0.224f) : new Vector3(0.5f, -0.053f, 0.228f))
                : (preserveAnimatedGrip ? new Vector3(0.515f, -0.315f, -0.259f) : new Vector3(0.502f, -0.333f, -0.219f));
            return hand.palm.position + along * coordinates.x + across * coordinates.y
                + Vector3.Cross(across, along).normalized * along.magnitude * coordinates.z;
        }

        private static Quaternion HandTurn((Transform palm, Transform fingers, Transform thumb) hand,
            Vector3 forward, Vector3 normal)
        {
            Vector3 along = hand.fingers.position - hand.palm.position;
            Vector3 across = hand.thumb.position - hand.palm.position;
            Quaternion basis = Quaternion.LookRotation(along, Vector3.Cross(across, along));
            return Quaternion.LookRotation(forward, normal) * Quaternion.Inverse(basis);
        }

        private static void CurlFingers(Transform fingers, Vector3 direction, float amount)
        {
            Quaternion curl = Quaternion.FromToRotation(fingers.up, direction);
            fingers.rotation = Quaternion.Slerp(Quaternion.identity, curl, amount) * fingers.rotation;
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
