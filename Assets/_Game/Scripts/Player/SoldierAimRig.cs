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
    [DefaultExecutionOrder(200)]
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
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            float overlayWeight = overlay != null ? overlay.Weight : 0f;
            float target = motor != null && motor.FacingCamera ? 1f - overlayWeight : 0f;
            weight = Mathf.MoveTowards(weight, target, blendSpeed * dt);

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

            Vector3 right = transform.right;
            Quaternion recoil = Quaternion.AngleAxis(-recoilPitch, right) * Quaternion.AngleAxis(recoilYaw, Vector3.up);
            Rotate(recoil * blendFix, pivot);
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
