using System.Collections.Generic;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ForestSoldier.UI
{
    /// <summary>
    /// Minimal shooter HUD, built in code so the prototype needs no UI assets:
    /// crosshair that opens with spread, hit marker, floating damage numbers,
    /// ammo and reload state, targets-down count with the reset prompt, and an
    /// FPS counter in the Editor and development builds.
    /// </summary>
    public class PrototypeHUD : MonoBehaviour
    {
        [SerializeField] private HitscanWeapon weapon;
        [SerializeField] private ThirdPersonCamera cameraRig;
        [SerializeField] private TargetResetController targets;
        [Tooltip("For keyboard vs. controller prompts.")]
        [SerializeField] private SoldierInput input;

        [Header("Style")]
        [SerializeField] private Color crosshairColor = new Color(1f, 1f, 1f, 0.9f);
        [SerializeField] private Color hitColor = Color.white;
        [SerializeField] private Color killColor = new Color(1f, 0.25f, 0.2f);
        [SerializeField] private Color critColor = new Color(1f, 0.85f, 0.2f);
        [SerializeField] private float crosshairBarLength = 9f;
        [SerializeField] private float crosshairBarWidth = 2f;
        [SerializeField] private float crosshairMinGap = 5f;
        [SerializeField] private float hitMarkerTime = 0.18f;
        [SerializeField] private float damageNumberTime = 0.7f;
        [SerializeField] private bool fpsCounter = true;

        private Canvas canvas;
        private RectTransform canvasRect;
        private Font font;
        private readonly RectTransform[] crossBars = new RectTransform[4];
        private RectTransform crosshairDot;
        private readonly Image[] hitBars = new Image[4];
        private RectTransform hitMarker;
        private float hitTimer;
        private Color hitTint;
        private Text ammoText, ammoHint, targetText, fpsText;
        private Image reloadBar, reloadBarBack;
        private float fpsSmoothed;
        private int shownAmmo = -1, shownDown = -1, shownTotal = -1, shownFps = -1, shownWalkersDown = -1;
        private string shownHint;
        private bool shownPad;
        private static readonly string[] FpsStrings = BuildFpsStrings();

        private static string[] BuildFpsStrings()
        {
            var a = new string[1000];
            for (int i = 0; i < a.Length; i++) a[i] = i + " fps";
            return a;
        }

        private class Floating
        {
            public Text Label;
            public Vector3 World;
            public float Age = float.MaxValue;
        }

        private readonly List<Floating> numbers = new List<Floating>();
        private int nextNumber;

        public int HitMarkersShown { get; private set; }

        private bool Pad => input != null && input.UsingGamepad;

        private void Awake()
        {
            if (input == null) input = FindFirstObjectByType<SoldierInput>();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Build();
        }

        private void OnEnable()
        {
            if (weapon != null) weapon.HitDamageable += OnHit;
        }

        private void OnDisable()
        {
            if (weapon != null) weapon.HitDamageable -= OnHit;
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            var go = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            canvas = go.GetComponent<Canvas>();
            // Camera space so the HUD also shows in camera captures; very near the lens.
            canvas.renderMode = cameraRig != null ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = cameraRig != null ? cameraRig.Camera : null;
            canvas.planeDistance = 0.08f;
            canvas.sortingOrder = 100;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;
            canvasRect = (RectTransform)go.transform;

            for (int i = 0; i < 4; i++)
            {
                var img = NewImage($"Crosshair{i}", canvasRect, crosshairColor);
                crossBars[i] = img.rectTransform;
                bool vertical = i % 2 == 0;
                crossBars[i].sizeDelta = vertical
                    ? new Vector2(crosshairBarWidth, crosshairBarLength)
                    : new Vector2(crosshairBarLength, crosshairBarWidth);
            }
            crosshairDot = NewImage("CrosshairDot", canvasRect, crosshairColor).rectTransform;
            crosshairDot.sizeDelta = new Vector2(2f, 2f);

            hitMarker = new GameObject("HitMarker", typeof(RectTransform)).GetComponent<RectTransform>();
            hitMarker.SetParent(canvasRect, false);
            for (int i = 0; i < 4; i++)
            {
                var img = NewImage($"Hit{i}", hitMarker, hitColor);
                img.rectTransform.sizeDelta = new Vector2(3f, 12f);
                float a = 45f + 90f * i;
                img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, a);
                img.rectTransform.anchoredPosition = Quaternion.Euler(0f, 0f, a) * Vector3.up * 14f;
                hitBars[i] = img;
            }
            hitMarker.gameObject.SetActive(false);

            ammoText = NewText("Ammo", canvasRect, 54, TextAnchor.LowerRight, new Vector2(1f, 0f), new Vector2(-48f, 40f));
            ammoHint = NewText("AmmoHint", canvasRect, 22, TextAnchor.LowerRight, new Vector2(1f, 0f), new Vector2(-50f, 108f));
            reloadBarBack = NewImage("ReloadBack", canvasRect, new Color(0f, 0f, 0f, 0.45f));
            Anchor(reloadBarBack.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -46f), new Vector2(120f, 5f));
            reloadBar = NewImage("Reload", reloadBarBack.rectTransform, new Color(1f, 1f, 1f, 0.85f));
            reloadBar.rectTransform.anchorMin = new Vector2(0f, 0f);
            reloadBar.rectTransform.anchorMax = new Vector2(0f, 1f);
            reloadBar.rectTransform.pivot = new Vector2(0f, 0.5f);
            reloadBar.rectTransform.anchoredPosition = Vector2.zero;
            reloadBarBack.gameObject.SetActive(false);

            targetText = NewText("Targets", canvasRect, 26, TextAnchor.UpperLeft, new Vector2(0f, 1f), new Vector2(40f, -36f));
            fpsText = NewText("FPS", canvasRect, 20, TextAnchor.UpperRight, new Vector2(1f, 1f), new Vector2(-30f, -30f));
            fpsText.gameObject.SetActive(fpsCounter && (Application.isEditor || Debug.isDebugBuild));

            for (int i = 0; i < 16; i++)
            {
                var t = NewText("Damage", canvasRect, 28, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero);
                t.gameObject.SetActive(false);
                numbers.Add(new Floating { Label = t });
            }
        }

        private Image NewImage(string name, Transform parent, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        private Text NewText(string name, Transform parent, int size, TextAnchor align, Vector2 anchor, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.color = Color.white;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            go.GetComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.6f);
            Anchor(t.rectTransform, anchor, pos, new Vector2(600f, size * 1.4f));
            t.rectTransform.pivot = anchor;
            return t;
        }

        private static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // ------------------------------------------------------------------ events

        private void OnHit(ShotResult shot, IDamageable target, DamageResult result)
        {
            HitMarkersShown++;
            hitTimer = hitMarkerTime;
            hitTint = result.Killed ? killColor : result.Critical ? critColor : hitColor;
            hitMarker.gameObject.SetActive(true);
            hitMarker.localScale = Vector3.one * (result.Killed ? 1.35f : 1f);

            var n = numbers[nextNumber];
            nextNumber = (nextNumber + 1) % numbers.Count;
            n.World = shot.Info.point + Vector3.up * 0.15f;
            n.Age = 0f;
            n.Label.text = Mathf.RoundToInt(result.Applied).ToString();
            n.Label.color = result.Critical ? critColor : Color.white;
            n.Label.fontSize = result.Critical ? 34 : 28;
            n.Label.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------------ frame

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            var cam = cameraRig != null ? cameraRig.Camera : Camera.main;
            bool showReticle = cameraRig == null || (!cameraRig.IsFrontView && !cameraRig.ShowsIronSights);
            if (crosshairDot.gameObject.activeSelf != showReticle)
            {
                crosshairDot.gameObject.SetActive(showReticle);
                foreach (var bar in crossBars) bar.gameObject.SetActive(showReticle);
            }

            // Crosshair gap = spread cone projected to pixels at the reference height.
            float gap = crosshairMinGap;
            if (weapon != null && cam != null)
            {
                float pxPerRad = 540f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                gap += Mathf.Tan(weapon.CurrentSpread * Mathf.Deg2Rad) * pxPerRad;
            }
            float half = crosshairBarLength * 0.5f;
            crossBars[0].anchoredPosition = new Vector2(0f, gap + half);
            crossBars[1].anchoredPosition = new Vector2(gap + half, 0f);
            crossBars[2].anchoredPosition = new Vector2(0f, -gap - half);
            crossBars[3].anchoredPosition = new Vector2(-gap - half, 0f);

            if (hitTimer > 0f)
            {
                hitTimer -= dt;
                var c = hitTint;
                c.a = Mathf.Clamp01(hitTimer / hitMarkerTime * 1.5f);
                foreach (var b in hitBars) b.color = c;
                if (hitTimer <= 0f) hitMarker.gameObject.SetActive(false);
            }

            if (weapon != null)
            {
                // Strings only change when the numbers do: no per-frame garbage.
                if (weapon.Ammo != shownAmmo)
                {
                    shownAmmo = weapon.Ammo;
                    ammoText.text = $"{weapon.Ammo} <size=30>/ {weapon.MagazineSize}</size>";
                    ammoText.color = weapon.Ammo == 0 ? killColor : Color.white;
                }
                string hint = weapon.IsReloading ? "RELOADING"
                    : weapon.Ammo == 0 ? (Pad ? "(X)  RELOAD" : "R  RELOAD")
                    : weapon.Ammo <= weapon.MagazineSize / 4 ? (Pad ? "(X)  reload" : "R  reload") : "";
                if (!ReferenceEquals(hint, shownHint)) ammoHint.text = shownHint = hint;
                reloadBarBack.gameObject.SetActive(weapon.IsReloading);
                if (weapon.IsReloading)
                    reloadBar.rectTransform.sizeDelta = new Vector2(120f * weapon.ReloadProgress, 0f);
            }

            if (targets != null && (targets.Down != shownDown || targets.Total != shownTotal || Pad != shownPad
                                    || targets.WalkersDown != shownWalkersDown))
            {
                shownPad = Pad;
                shownWalkersDown = targets.WalkersDown;
                shownDown = targets.Down;
                shownTotal = targets.Total;
                string walkers = targets.WalkersTotal > 0 ? $"\nWalkers down  {shownWalkersDown} / {targets.WalkersTotal}" : "";
                targetText.text = $"Targets down  {shownDown} / {shownTotal}{walkers}\n<size=20>" + (Pad ? "View  reset all" : "T  reset all") + "</size>";
            }

            if (cam != null)
            {
                foreach (var n in numbers)
                {
                    if (n.Age >= damageNumberTime) continue;
                    n.Age += dt;
                    if (n.Age >= damageNumberTime)
                    {
                        n.Label.gameObject.SetActive(false);
                        continue;
                    }
                    Vector3 sp = cam.WorldToScreenPoint(n.World + Vector3.up * (n.Age * 0.6f));
                    if (sp.z <= 0f)
                    {
                        n.Label.enabled = false;
                        continue;
                    }
                    n.Label.enabled = true;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, sp,
                        canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : cam, out Vector2 local);
                    n.Label.rectTransform.anchoredPosition = local;
                    var col = n.Label.color;
                    col.a = 1f - n.Age / damageNumberTime;
                    n.Label.color = col;
                }
            }

            if (fpsText.gameObject.activeSelf && dt > 0f)
            {
                fpsSmoothed = fpsSmoothed <= 0f ? 1f / dt : Mathf.Lerp(fpsSmoothed, 1f / dt, 0.05f);
                int fps = Mathf.RoundToInt(fpsSmoothed);
                if (fps != shownFps) fpsText.text = FpsStrings[shownFps = Mathf.Clamp(fps, 0, FpsStrings.Length - 1)];
            }
        }
    }
}
