using ForestSoldier.Combat;
using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// After the Animator (and the reload overlay), turns the whole upper body
    /// as one rigid piece about the chest so the barrel points at the crosshair,
    /// then adds recoil on top.
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
            if (weight <= 1e-4f && Mathf.Abs(recoilPitch) < 0.01f && Mathf.Abs(recoilYaw) < 0.01f) return;

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

            if (weight > 1e-4f && RaiseRifle())
            {
                for (int i = 0; i < 3; i++)
                {
                    Quaternion step = AimStep();
                    if (Quaternion.Angle(step, Quaternion.identity) < 0.05f) break;
                    Rotate(Quaternion.Slerp(Quaternion.identity, step, weight), pivot);
                }
            }

            Vector3 right = transform.right;
            Quaternion recoil = Quaternion.AngleAxis(-recoilPitch, right) * Quaternion.AngleAxis(recoilYaw, Vector3.up);
            Rotate(recoil, pivot);
        }

        private bool RaiseRifle()
        {
            if (head == null || rifle == null || leftArm.wrist == null || rightArm.wrist == null ||
                leftArm.shoulder == null || leftArm.elbow == null || rightArm.shoulder == null || rightArm.elbow == null)
                return false;

            // Compare the barrel line with the head at the same depth. This
            // keeps the sights under the eyes when looking up or down.
            Vector3 barrel = muzzle.forward;
            Vector3 horizontal = Vector3.ProjectOnPlane(barrel, Vector3.up);
            if (horizontal.sqrMagnitude < 0.05f) return false;
            float headDepth = Vector3.Dot(head.position - muzzle.position, horizontal) / horizontal.sqrMagnitude;
            float barrelAtHead = muzzle.position.y + barrel.y * headDepth;
            float lift = Mathf.Clamp(head.position.y + 0.015f - barrelAtHead, 0f, 0.30f) * weight;
            lift = Mathf.Min(lift, MaxLift(leftArm), MaxLift(rightArm));
            if (lift < 0.001f) return false;

            Vector3 offset = Vector3.up * lift;
            rifle.position += offset;
            PlaceArm(leftArm, offset);
            PlaceArm(rightArm, offset);
            return true;
        }

        private static float MaxLift((Transform shoulder, Transform elbow, Transform wrist) arm)
        {
            Vector3 reach = arm.wrist.position - arm.shoulder.position;
            float length = Vector3.Distance(arm.shoulder.position, arm.elbow.position)
                + Vector3.Distance(arm.elbow.position, arm.wrist.position) - 0.002f;
            float horizontalSquared = reach.x * reach.x + reach.z * reach.z;
            return Mathf.Sqrt(Mathf.Max(0f, length * length - horizontalSquared)) - reach.y;
        }

        private static void PlaceArm((Transform shoulder, Transform elbow, Transform wrist) arm, Vector3 offset)
        {
            Vector3 shoulder = arm.shoulder.position;
            Vector3 elbow = arm.elbow.position;
            Vector3 wrist = arm.wrist.position;
            float upperLength = Vector3.Distance(shoulder, elbow);
            float lowerLength = Vector3.Distance(elbow, wrist);
            Vector3 target = wrist + offset;
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
            arm.wrist.position = target;
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
