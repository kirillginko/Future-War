using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Plays weapon carry, aim and reload poses over the current locomotion.
    ///
    /// An Avatar Mask layer can't do this on this rig: the hands and SMG are
    /// parented to the root, so they follow the base clip's root while the arms
    /// follow the overlay clip's chest, and the gun comes out of the hands.
    /// A hidden copy of the skeleton samples the clips, and each frame
    /// its upper body is copied onto the real one, re-seated on the real chest.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class UpperBodyOverlay : MonoBehaviour
    {
        [Tooltip("The soldier model asset (the FBX); a hidden copy plays the overlay clips.")]
        [SerializeField] private GameObject proxyModel;
        [Tooltip("The visible model under the player.")]
        [SerializeField] private Transform model;
        [SerializeField] private AnimationClip standingAimClip;
        [SerializeField] private AnimationClip crouchingAimClip;
        [SerializeField] private AnimationClip reloadClip;
        [SerializeField] private float blendIn = 0.15f;
        [SerializeField] private float blendOut = 0.25f;

        private static readonly string[] RootLevelUpper = { "HandWrist.L", "HandWrist.R", "PoleArm.L", "PoleArm.R", "SMGBone" };

        private SoldierInput input;
        private SoldierMotor motor;
        private Transform proxyRoot;
        private Transform realChest, proxyChest;
        private Transform realHead;
        private readonly List<(Transform real, Transform proxy)> rootLevel = new List<(Transform, Transform)>();
        private readonly List<(Transform real, Transform proxy)> locals = new List<(Transform, Transform)>();
        private float elapsed, duration;
        private bool playing;
        private float aimWeight, aimTime, carryPitch;

        public float Weight { get; private set; }
        public bool IsPlaying => playing;

        private void Awake()
        {
            if (proxyModel == null || model == null)
            {
                Debug.LogWarning($"{name}: UpperBodyOverlay is missing its model references.", this);
                enabled = false;
                return;
            }
            input = GetComponent<SoldierInput>();
            motor = GetComponent<SoldierMotor>();
            var proxy = Instantiate(proxyModel, transform);
            proxy.name = "OverlayProxy (hidden)";
            proxyRoot = proxy.transform;
            proxyRoot.SetLocalPositionAndRotation(model.localPosition, model.localRotation);
            proxyRoot.localScale = model.localScale;
            foreach (var r in proxy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            proxy.GetComponent<Animator>().enabled = false;

            var real = Index(model);
            var prox = Index(proxyRoot);
            realChest = real["Spineupper"];
            proxyChest = prox["Spineupper"];
            real.TryGetValue("head", out realHead);
            foreach (var n in RootLevelUpper)
                if (real.TryGetValue(n, out var a) && prox.TryGetValue(n, out var b)) rootLevel.Add((a, b));
            // Everything under the chest and the hands keeps the overlay clip's local pose.
            AddDescendants(realChest, prox);
            foreach (var n in RootLevelUpper)
                if (real.TryGetValue(n, out var a)) AddDescendants(a, prox);
        }

        private void AddDescendants(Transform realParent, Dictionary<string, Transform> prox)
        {
            foreach (Transform c in realParent)
            {
                if (prox.TryGetValue(c.name, out var p)) locals.Add((c, p));
                AddDescendants(c, prox);
            }
        }

        private static Dictionary<string, Transform> Index(Transform root)
        {
            var d = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) d[t.name] = t;
            return d;
        }

        /// <summary>Play the reload clip, stretched to last <paramref name="seconds"/>.</summary>
        public void PlayReload(float seconds)
        {
            if (!enabled || reloadClip == null) return;
            duration = Mathf.Max(0.1f, seconds);
            elapsed = 0f;
            playing = true;
        }

        public void Stop()
        {
            playing = false;
            Weight = 0f;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            bool crouching = motor != null && motor.IsCrouching;
            bool sprinting = motor != null && motor.IsSprinting;
            bool posing = motor != null && (motor.FacingCamera || crouching || sprinting);
            bool firing = input != null && input.Fire;
            aimWeight = firing ? 1f : Mathf.MoveTowards(aimWeight, posing ? 1f : 0f, dt / blendIn);
            carryPitch = firing ? 0f : Mathf.MoveTowards(carryPitch, sprinting ? 22f : 0f, 180f * dt);
            var aimClip = crouching ? crouchingAimClip : standingAimClip;

            proxyRoot.SetLocalPositionAndRotation(model.localPosition, model.localRotation);
            if (aimWeight > 0f && aimClip != null)
            {
                aimTime += dt;
                aimClip.SampleAnimation(proxyRoot.gameObject, Mathf.Repeat(aimTime, aimClip.length));
                ApplyPose(aimWeight);
                if (carryPitch > 0.01f)
                    LowerRifle(carryPitch * aimWeight);
            }
            else aimTime = 0f;

            if (!playing) return;
            elapsed += dt;
            if (elapsed >= duration)
            {
                Stop();
                return;
            }

            Weight = Mathf.Clamp01(Mathf.Min(elapsed / blendIn, (duration - elapsed) / blendOut));
            reloadClip.SampleAnimation(proxyRoot.gameObject, reloadClip.length * (elapsed / duration));
            ApplyPose(Weight);
        }

        private void LowerRifle(float degrees)
        {
            Quaternion tilt = Quaternion.AngleAxis(degrees, transform.right);
            Vector3 pivot = realChest.position;
            Quaternion headRotation = realHead != null ? realHead.rotation : Quaternion.identity;
            realChest.rotation = tilt * realChest.rotation;
            foreach (var (bone, _) in rootLevel)
                bone.SetPositionAndRotation(pivot + tilt * (bone.position - pivot), tilt * bone.rotation);
            if (realHead != null) realHead.rotation = headRotation;
        }

        private void ApplyPose(float w)
        {
            Vector3 chestPos = realChest.position;
            Vector3 proxyChestPos = proxyChest.position;
            realChest.rotation = Quaternion.Slerp(realChest.rotation, proxyChest.rotation, w);
            foreach (var (r, p) in rootLevel)
            {
                Vector3 pos = chestPos + (p.position - proxyChestPos);
                r.SetPositionAndRotation(Vector3.Lerp(r.position, pos, w), Quaternion.Slerp(r.rotation, p.rotation, w));
            }
            foreach (var (r, p) in locals)
                r.SetLocalPositionAndRotation(Vector3.Lerp(r.localPosition, p.localPosition, w),
                    Quaternion.Slerp(r.localRotation, p.localRotation, w));
        }
    }
}
