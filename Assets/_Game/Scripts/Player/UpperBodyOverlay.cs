using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.Player
{
    /// <summary>
    /// Plays an upper-body clip (the reload) over whatever the legs are doing.
    ///
    /// An Avatar Mask layer can't do this on this rig: the hands and SMG are
    /// parented to the root, so they follow the base clip's root while the arms
    /// follow the overlay clip's chest, and the gun comes out of the hands.
    /// Instead, a hidden copy of the skeleton plays the clip, and each frame
    /// its upper body is copied onto the real one, re-seated on the real chest.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class UpperBodyOverlay : MonoBehaviour
    {
        [Tooltip("The soldier model asset (the FBX); a hidden copy plays the overlay clips.")]
        [SerializeField] private GameObject proxyModel;
        [SerializeField] private RuntimeAnimatorController overlayController;
        [Tooltip("The visible model under the player.")]
        [SerializeField] private Transform model;
        [SerializeField] private string reloadState = "Reload";
        [SerializeField] private AnimationClip reloadClip;
        [SerializeField] private float blendIn = 0.15f;
        [SerializeField] private float blendOut = 0.25f;

        private static readonly string[] ChestRoot = { "Spineupper" };
        private static readonly string[] RootLevelUpper = { "HandWrist.L", "HandWrist.R", "PoleArm.L", "PoleArm.R", "SMGBone" };

        private Transform proxyRoot;
        private Animator proxyAnimator;
        private Transform realChest, proxyChest;
        private readonly List<(Transform real, Transform proxy)> rootLevel = new List<(Transform, Transform)>();
        private readonly List<(Transform real, Transform proxy)> locals = new List<(Transform, Transform)>();
        private float elapsed, duration;
        private bool playing;

        public float Weight { get; private set; }
        public bool IsPlaying => playing;

        private void Awake()
        {
            if (proxyModel == null || model == null || overlayController == null)
            {
                Debug.LogWarning($"{name}: UpperBodyOverlay is missing references; reloads will not animate.", this);
                enabled = false;
                return;
            }
            var proxy = Instantiate(proxyModel, transform);
            proxy.name = "OverlayProxy (hidden)";
            proxyRoot = proxy.transform;
            proxyRoot.SetLocalPositionAndRotation(model.localPosition, model.localRotation);
            proxyRoot.localScale = model.localScale;
            foreach (var r in proxy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            proxyAnimator = proxy.GetComponent<Animator>();
            proxyAnimator.runtimeAnimatorController = overlayController;
            proxyAnimator.applyRootMotion = false;
            proxyAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            proxyAnimator.enabled = false;

            var real = Index(model);
            var prox = Index(proxyRoot);
            realChest = real["Spineupper"];
            proxyChest = prox["Spineupper"];
            foreach (var n in RootLevelUpper)
                if (real.TryGetValue(n, out var a) && prox.TryGetValue(n, out var b)) rootLevel.Add((a, b));
            // Everything under the chest and the hands keeps the overlay clip's local pose.
            foreach (var n in ChestRoot) AddDescendants(real[n], prox);
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
            if (!enabled) return;
            duration = Mathf.Max(0.1f, seconds);
            elapsed = 0f;
            playing = true;
            proxyAnimator.enabled = true;
            proxyAnimator.speed = reloadClip != null ? reloadClip.length / duration : 1f;
            proxyAnimator.Play(reloadState, 0, 0f);
            proxyAnimator.Update(0f);
        }

        public void Stop()
        {
            playing = false;
            Weight = 0f;
            if (proxyAnimator != null) proxyAnimator.enabled = false;
        }

        private void LateUpdate()
        {
            if (!playing) return;
            elapsed += Time.deltaTime;
            if (elapsed >= duration)
            {
                Stop();
                return;
            }
            Weight = Mathf.Clamp01(Mathf.Min(elapsed / blendIn, (duration - elapsed) / blendOut));
            float w = Weight;

            // The proxy stands where the model stands, facing where the player faces.
            proxyRoot.SetLocalPositionAndRotation(model.localPosition, model.localRotation);

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
