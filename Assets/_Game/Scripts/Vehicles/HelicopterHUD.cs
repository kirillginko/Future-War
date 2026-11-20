using System.Collections.Generic;
using ForestSoldier.Combat;
using ForestSoldier.NPC;
using ForestSoldier.UI;
using UnityEngine;
using UnityEngine.UI;

namespace ForestSoldier.Vehicles
{
    /// <summary>
    /// The Little Bird's flight HUD and targeting display, in the style of a
    /// gunship HUD in Battlefield: green vector lines over the view.
    ///
    /// - Heading tape across the top, speed (km/h) on the left, height and climb
    ///   rate on the right with a radar-altitude scale.
    /// - Attitude indicator centred on the helicopter, its pitch ladder running
    ///   either side of it (never across it), banking the same way (half the angle).
    /// - Gun reticle where the rounds actually go (it slides off the crosshair
    ///   past the guns' swing), with a spin-up ring; a rocket marker where a
    ///   rocket fired now would land.
    /// - Targets: a red diamond over every live target and walker ahead; the
    ///   weapons' locked target gets pulsing brackets, LOCK and its range (the
    ///   guns fire on it).
    /// - Hit and kill markers, rocket pips and rearming, rotor spool, and a
    ///   PULL UP warning when sinking fast near the ground.
    ///
    /// The soldier's own HUD hides while you fly. On foot it shows the "get in"
    /// prompt by the helicopter.
    /// </summary>
    [DefaultExecutionOrder(100)] // after the chase camera has moved
    public class HelicopterHUD : MonoBehaviour
    {
        [SerializeField] private HelicopterPilot pilot;
        [SerializeField] private HelicopterWeapons weapons;
        [SerializeField] private Color hudColour = new Color(0.55f, 1f, 0.65f, 0.92f);
        [SerializeField] private Color dimColour = new Color(0.55f, 1f, 0.65f, 0.45f);
        [SerializeField] private Color enemyColour = new Color(1f, 0.33f, 0.24f, 0.95f);
        [SerializeField] private Color warnColour = new Color(1f, 0.75f, 0.2f, 1f);
        [Tooltip("Targets beyond this range (m) get no marker.")]
        [SerializeField] private float targetRange = 400f;

        [Header("Attitude indicator")]
        [Tooltip("Clearance either side of the helicopter's body on screen, canvas pixels.")]
        [SerializeField] private float attitudeGap = 30f;
        [Tooltip("The smallest half-gap in the middle, canvas pixels (when the helicopter is small on screen).")]
        [SerializeField] private float attitudeMinGap = 110f;
        [Tooltip("Length of each pitch line beside the helicopter, canvas pixels (the horizon is half as long again).")]
        [SerializeField] private float attitudeLength = 150f;
        [Tooltip("Share of the bank it shows (it tilts the same way as the helicopter).")]
        [SerializeField] private float attitudeBank = 0.5f;
        [Tooltip("Canvas pixels per degree of pitch.")]
        [SerializeField] private float attitudePxPerDeg = 4.5f;
        [Tooltip("The ladder shows through a window this many pixels above and below it; lines outside are hidden.")]
        [SerializeField] private float attitudeRange = 190f;

        private Canvas canvas;
        private RectTransform canvasRect;
        private HudLines lines;
        private Text prompt, flight, speedText, speedUnit, altText, climbText, headingText, weaponText, rocketText,
            warnText, rangeText, killText;
        private Text[] flightTexts;
        private readonly List<Text> headingLabels = new List<Text>();
        private readonly List<Text> ladderLabels = new List<Text>();
        private Camera cam;
        private PrototypeHUD soldierHud;
        private Collider[] bodyColliders; // cabin, skids, pods: the indicator's gap fits round these
        private readonly Vector3[] corners = new Vector3[8];
        private int shownKey = int.MinValue;
        private float hitFlash, killFlash;

        public string PromptText => prompt != null ? prompt.text : "";
        public string FlightText => flight != null ? flight.text : "";
        /// <summary>Targets marked on the HUD this frame, and the one locked (null for none).</summary>
        public int MarkedTargets { get; private set; }
        public Transform LockedTarget => weapons != null ? weapons.LockedTarget : null;
        public bool GunReticleOnScreen { get; private set; }
        public bool RocketMarkerOnScreen { get; private set; }
        public int LineCount => lines != null ? lines.Count : 0;

        private void Awake()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("Helicopter HUD", typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;
            canvasRect = (RectTransform)go.transform;

            var lgo = new GameObject("Lines", typeof(RectTransform), typeof(CanvasRenderer), typeof(HudLines));
            lgo.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)lgo.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            lines = lgo.GetComponent<HudLines>();
            lines.raycastTarget = false;

            prompt = Label("Prompt", font, 30, TextAnchor.MiddleCenter, new Vector2(0f, -120f), Color.white);
            flight = Label("Controls", font, 18, TextAnchor.MiddleLeft, Vector2.zero, dimColour);
            speedText = Label("Speed", font, 34, TextAnchor.MiddleRight, Vector2.zero, hudColour);
            speedUnit = Label("SpeedUnit", font, 18, TextAnchor.MiddleRight, Vector2.zero, hudColour);
            altText = Label("Altitude", font, 34, TextAnchor.MiddleLeft, Vector2.zero, hudColour);
            climbText = Label("Climb", font, 18, TextAnchor.MiddleLeft, Vector2.zero, hudColour);
            headingText = Label("Heading", font, 24, TextAnchor.MiddleCenter, Vector2.zero, hudColour);
            weaponText = Label("Weapon", font, 22, TextAnchor.MiddleRight, Vector2.zero, hudColour);
            rocketText = Label("Rockets", font, 22, TextAnchor.MiddleRight, Vector2.zero, hudColour);
            warnText = Label("Warning", font, 40, TextAnchor.MiddleCenter, Vector2.zero, warnColour);
            rangeText = Label("Range", font, 20, TextAnchor.MiddleLeft, Vector2.zero, enemyColour);
            killText = Label("Kill", font, 26, TextAnchor.MiddleCenter, Vector2.zero, enemyColour);
            for (int i = 0; i < 12; i++)
                headingLabels.Add(Label("HeadingLabel", font, 18, TextAnchor.MiddleCenter, Vector2.zero, hudColour));
            for (int i = 0; i < 8; i++)
                ladderLabels.Add(Label("LadderLabel", font, 16, TextAnchor.MiddleLeft, Vector2.zero, dimColour));
            flightTexts = new[] { flight, speedText, speedUnit, altText, climbText, headingText, weaponText, rocketText, warnText, rangeText, killText };
        }

        private void Start()
        {
            if (pilot == null) pilot = FindFirstObjectByType<HelicopterPilot>();
            if (weapons == null && pilot != null) weapons = pilot.GetComponent<HelicopterWeapons>();
            soldierHud = FindFirstObjectByType<PrototypeHUD>();
            if (weapons != null) weapons.HitDamageable += OnHit;
            if (pilot != null)
            {
                var all = pilot.GetComponentsInChildren<Collider>(true);
                var keep = new List<Collider>();
                foreach (var col in all) if (col.name != "TailBoom") keep.Add(col);
                bodyColliders = keep.ToArray();
            }
            cam = Camera.main;
            if (cam != null)
            {
                // Camera space, like the soldier's HUD: it also shows in camera captures.
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 0.085f;
            }
        }

        private void OnDestroy()
        {
            if (weapons != null) weapons.HitDamageable -= OnHit;
        }

        private void OnHit(ShotResult shot, IDamageable target, DamageResult result)
        {
            hitFlash = 0.14f;
            if (result.Killed) killFlash = 1.2f;
        }

        private Text Label(string name, Font font, int size, TextAnchor align, Vector2 pos, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(canvasRect, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.color = colour;
            t.raycastTarget = false;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            go.GetComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.55f);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = align == TextAnchor.MiddleLeft ? new Vector2(0f, 0.5f)
                : align == TextAnchor.MiddleRight ? new Vector2(1f, 0.5f) : new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(900f, size * 2f);
            return t;
        }

        private static void Place(Text t, Vector2 pos, string text, bool show = true)
        {
            if (t.gameObject.activeSelf != show) t.gameObject.SetActive(show);
            if (!show) return;
            t.rectTransform.anchoredPosition = pos;
            if (t.text != text) t.text = text;
        }

        // ---------------------------------------------------------------- frame

        private void LateUpdate()
        {
            if (pilot == null || lines == null) return;
            hitFlash -= Time.deltaTime;
            killFlash -= Time.deltaTime;
            bool pad = pilot.Input != null && pilot.Input.UsingGamepad;
            bool flying = pilot.Occupied;
            if (soldierHud != null && soldierHud.Hidden != flying) soldierHud.Hidden = flying;

            string p = !pilot.PlayerInRange ? "" : pad ? "Y  fly the Little Bird" : "F  fly the Little Bird";
            if (prompt.text != p) prompt.text = p;

            lines.Begin();
            MarkedTargets = 0;
            GunReticleOnScreen = RocketMarkerOnScreen = false;
            if (flying && (cam == null || !cam.isActiveAndEnabled)) cam = Camera.main;
            if (!flying || cam == null)
            {
                foreach (var t in flightTexts) if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                foreach (var t in headingLabels) if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                foreach (var t in ladderLabels) if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                if (flight.text != "") flight.text = "";
                shownKey = int.MinValue;
                lines.End();
                return;
            }
            Draw(canvasRect.rect.size, pad);
            lines.End();
        }

        /// <summary>World point to canvas position (centre = 0, 0); false if it is behind the camera.</summary>
        private bool ToCanvas(Vector3 world, Vector2 size, out Vector2 p)
        {
            Vector3 v = cam.WorldToViewportPoint(world);
            p = new Vector2((v.x - 0.5f) * size.x, (v.y - 0.5f) * size.y);
            return v.z > 0.1f;
        }

        private void Draw(Vector2 size, bool pad)
        {
            var heli = pilot.transform;
            var body = pilot.Body;
            Color c = hudColour, dim = dimColour;
            float halfW = size.x * 0.5f, halfH = size.y * 0.5f;

            // ---- attitude indicator: full size and centred on the helicopter, with a gap in the middle
            // as wide as its body, so the ladder runs either side of it instead of across it. It banks
            // the same way the helicopter does (half the angle); the ladder slides with the pitch.
            int ladderLabel = 0;
            if (ToCanvas(heli.position + Vector3.up * 1.2f, size, out Vector2 centre))
            {
                // The body's extent on screen (cabin, skids, pods; the thin tail boom and rotor don't count).
                float minX = centre.x, maxX = centre.x, minY = centre.y, maxY = centre.y;
                if (bodyColliders != null)
                    foreach (var col in bodyColliders)
                    {
                        if (col == null) continue;
                        Bounds bb = col.bounds;
                        for (int k = 0; k < 8; k++)
                        {
                            Vector3 corner = bb.center + Vector3.Scale(bb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                            if (!ToCanvas(corner, size, out Vector2 q)) continue;
                            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
                            minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
                        }
                    }
                centre = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                float gap = Mathf.Max(attitudeMinGap, (maxX - minX) * 0.5f + attitudeGap);

                float pitchUp = -Mathf.DeltaAngle(0f, heli.eulerAngles.x);
                float roll = Mathf.DeltaAngle(0f, heli.eulerAngles.z);
                Quaternion rot = Quaternion.Euler(0f, 0f, roll * attitudeBank);

                // The helicopter's own level: two short bars either side of it (they don't tilt).
                for (int side = -1; side <= 1; side += 2)
                {
                    lines.Line(centre + new Vector2(side * (gap - 26f), 0f), centre + new Vector2(side * (gap - 6f), 0f), c, 4f);
                    lines.Line(centre + new Vector2(side * (gap - 6f), 0f), centre + new Vector2(side * (gap - 6f), -8f), c, 3f);
                }

                for (int deg = -40; deg <= 40; deg += 10)
                {
                    float y = (deg - pitchUp) * attitudePxPerDeg;
                    bool horizon = deg == 0;
                    if (Mathf.Abs(y) > attitudeRange)
                    {
                        if (!horizon) continue;
                        y = Mathf.Clamp(y, -attitudeRange, attitudeRange); // the horizon waits at the window's edge
                    }
                    float inner = gap, outer = gap + (horizon ? attitudeLength * 1.5f : attitudeLength);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector2 a = centre + (Vector2)(rot * new Vector3(inner * side, y));
                        Vector2 b = centre + (Vector2)(rot * new Vector3(outer * side, y));
                        if (deg < 0) lines.Dashed(a, b, dim, 12f, 8f, 2f);
                        else lines.Line(a, b, horizon ? c : dim, horizon ? 2.5f : 2f);
                        if (!horizon) lines.Line(a, a + (Vector2)(rot * new Vector3(0f, deg > 0 ? -9f : 9f)), dim, 2f);
                    }
                    if (!horizon && ladderLabel < ladderLabels.Count)
                        Place(ladderLabels[ladderLabel++], centre + (Vector2)(rot * new Vector3(outer + 8f, y)), Mathf.Abs(deg).ToString());
                }
            }
            for (; ladderLabel < ladderLabels.Count; ladderLabel++) Place(ladderLabels[ladderLabel], Vector2.zero, "", false);
            c = hudColour;

            // ---- heading tape
            float heading = (heli.eulerAngles.y + 360f) % 360f;
            float tapeY = halfH - 70f, tapeHalf = 320f, pxPerHeadingDeg = 4f;
            int label = 0;
            for (int deg = Mathf.FloorToInt((heading - 85f) / 5f) * 5; deg <= heading + 85f; deg += 5)
            {
                float x = (deg - heading) * pxPerHeadingDeg;
                if (Mathf.Abs(x) > tapeHalf) continue;
                int d = ((deg % 360) + 360) % 360;
                bool major = d % 10 == 0;
                lines.Line(new Vector2(x, tapeY), new Vector2(x, tapeY + (major ? 14f : 7f)), major ? c : dim, 2f);
                if (d % 30 == 0 && label < headingLabels.Count && Mathf.Abs(x) > 30f)
                {
                    string name = d == 0 ? "N" : d == 90 ? "E" : d == 180 ? "S" : d == 270 ? "W" : (d / 10).ToString("00");
                    Place(headingLabels[label++], new Vector2(x, tapeY + 30f), name);
                }
            }
            for (; label < headingLabels.Count; label++) Place(headingLabels[label], Vector2.zero, "", false);
            lines.Line(new Vector2(-tapeHalf, tapeY), new Vector2(tapeHalf, tapeY), dim, 1.5f);
            lines.Line(new Vector2(0f, tapeY - 4f), new Vector2(-8f, tapeY - 16f), c, 2.5f);
            lines.Line(new Vector2(0f, tapeY - 4f), new Vector2(8f, tapeY - 16f), c, 2.5f);
            Place(headingText, new Vector2(0f, tapeY - 34f), Mathf.RoundToInt(heading % 360f).ToString("000"));

            // ---- speed (left) and height (right)
            float boxX = Mathf.Min(halfW - 220f, 430f);
            int kmh = Mathf.RoundToInt(pilot.Speed * 3.6f);
            lines.Box(new Vector2(-boxX - 55f, 0f), new Vector2(62f, 24f), c, 2f);
            Place(speedText, new Vector2(-boxX - 4f, 0f), kmh.ToString());
            Place(speedUnit, new Vector2(-boxX - 4f, -40f), "KM/H");
            float alt = pilot.Altitude;
            float vy = body != null ? body.linearVelocity.y : 0f;
            lines.Box(new Vector2(boxX + 55f, 0f), new Vector2(62f, 24f), c, 2f);
            Place(altText, new Vector2(boxX + 4f, 0f), Mathf.RoundToInt(alt).ToString());
            Place(climbText, new Vector2(boxX + 4f, -40f), $"M    {(vy >= 0f ? "+" : "")}{vy:F1}");
            // Radar altitude scale, 0..60 m, with a caret.
            float scaleX = boxX + 150f, scaleH = 150f;
            lines.Line(new Vector2(scaleX, -scaleH), new Vector2(scaleX, scaleH), dim, 1.5f);
            for (int m = 0; m <= 60; m += 10)
            {
                float y = -scaleH + 2f * scaleH * m / 60f;
                lines.Line(new Vector2(scaleX, y), new Vector2(scaleX + (m % 20 == 0 ? 14f : 8f), y), dim, 1.5f);
            }
            float caret = -scaleH + 2f * scaleH * Mathf.Clamp01(alt / 60f);
            lines.Line(new Vector2(scaleX - 4f, caret), new Vector2(scaleX - 16f, caret + 7f), c, 2.5f);
            lines.Line(new Vector2(scaleX - 4f, caret), new Vector2(scaleX - 16f, caret - 7f), c, 2.5f);

            // ---- the crosshair (where you look) and the gun reticle (where the rounds go)
            lines.Line(new Vector2(-6f, 0f), new Vector2(6f, 0f), dim, 2f);
            lines.Line(new Vector2(0f, -6f), new Vector2(0f, 6f), dim, 2f);
            Vector2 gun = Vector2.zero;
            if (weapons != null && ToCanvas(weapons.GunImpactPoint, size, out gun))
            {
                GunReticleOnScreen = true;
                Color gc = hitFlash > 0f ? Color.white : c;
                lines.Arc(gun, 26f, gc, 0f, 360f, 36, 2f);
                lines.Line(gun + new Vector2(-1.5f, 0f), gun + new Vector2(1.5f, 0f), gc, 3f);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 dir = Quaternion.Euler(0f, 0f, i * 90f) * Vector2.up;
                    lines.Line(gun + dir * 26f, gun + dir * 36f, gc, 2f);
                }
                if (weapons.Spin > 0f) lines.Arc(gun, 31f, c, 0f, 360f * weapons.Spin, 36, 3f);
                if (hitFlash > 0f || killFlash > 0.9f)
                {
                    Color hc = killFlash > 0.9f ? enemyColour : Color.white;
                    for (int i = 0; i < 4; i++)
                    {
                        Vector2 dir = Quaternion.Euler(0f, 0f, 45f + i * 90f) * Vector2.up;
                        lines.Line(gun + dir * 10f, gun + dir * 21f, hc, 3f);
                    }
                }
            }

            // ---- rocket marker: where one fired now would land
            if (weapons != null && weapons.Rockets > 0 && weapons.PredictRocket(out Vector3 impact) && ToCanvas(impact, size, out Vector2 rk))
            {
                RocketMarkerOnScreen = true;
                lines.Diamond(rk, 14f, c, 2f);
                lines.Line(rk + new Vector2(0f, 14f), rk + new Vector2(0f, 26f), c, 2f);
                lines.Line(rk - new Vector2(0f, 14f), rk - new Vector2(0f, 26f), c, 2f);
            }

            // ---- targets: a diamond on each; the weapons' lock gets brackets, its range and LOCK
            Vector3 eye = cam.transform.position;
            foreach (var t in DamageableTarget.All)
                if (t != null && t.IsAlive) Mark(t.transform.position + Vector3.up * 1f, size, eye);
            foreach (var w in WalkerNPC.All)
                if (w != null && w.IsAlive && w.isActiveAndEnabled) Mark(w.transform.position + Vector3.up * 1.1f, size, eye);
            if (weapons != null && weapons.LockedTarget != null && ToCanvas(weapons.LockPoint, size, out Vector2 lockPos))
            {
                float dist = Vector3.Distance(eye, weapons.LockPoint);
                float h = Mathf.Clamp(900f / Mathf.Max(5f, dist), 18f, 60f);
                float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 10f);
                lines.Brackets(lockPos, new Vector2(h, h) * pulse, h * 0.4f, enemyColour, 2.5f);
                lines.Diamond(lockPos, 4f, enemyColour, 2f);
                Place(rangeText, lockPos + new Vector2(h + 10f, h * 0.6f), $"LOCK  {Mathf.RoundToInt(dist)} m");
            }
            else Place(rangeText, Vector2.zero, "", false);
            Place(killText, new Vector2(0f, -110f), "KILL", killFlash > 0f);

            // ---- weapons panel (bottom centre)
            float panelY = -halfH + 120f;
            bool firing = pilot.Input != null && pilot.Input.Fire;
            Place(weaponText, new Vector2(-100f, panelY + 26f), firing ? "<b>MINIGUN</b>" : "MINIGUN");
            lines.Box(new Vector2(10f, panelY + 26f), new Vector2(90f, 6f), dim, 1.5f);
            if (weapons != null && weapons.Spin > 0f)
                lines.Line(new Vector2(-80f, panelY + 26f), new Vector2(-80f + 180f * weapons.Spin, panelY + 26f), c, 8f);
            int rockets = weapons != null ? weapons.Rockets : 0, capacity = weapons != null ? weapons.RocketCapacity : 0;
            bool rearming = pilot.OnGround && rockets < capacity;
            Place(rocketText, new Vector2(-100f, panelY - 10f), rearming && Mathf.Repeat(Time.time, 0.8f) < 0.5f ? "REARMING" : "ROCKETS");
            for (int i = 0; i < capacity; i++)
            {
                float x = -76f + i * 13f;
                if (i < rockets) lines.Line(new Vector2(x, panelY - 18f), new Vector2(x, panelY - 2f), c, 6f);
                else lines.Box(new Vector2(x, panelY - 10f), new Vector2(3f, 8f), dim, 1f);
            }

            // ---- rotor spool and warnings
            float rotor = pilot.RotorSpeed;
            if (rotor < 1f)
                Place(warnText, new Vector2(0f, 150f), $"ROTOR  {Mathf.RoundToInt(rotor * 100f)}%");
            else if (!pilot.OnGround && alt < 12f && vy < -6f && Mathf.Repeat(Time.time, 0.5f) < 0.3f)
                Place(warnText, new Vector2(0f, 150f), "PULL UP");
            else Place(warnText, Vector2.zero, "", false);

            // ---- controls hint (bottom left)
            int key = pad ? 1 : 2;
            if (key != shownKey)
            {
                shownKey = key;
                flight.text = pad
                    ? "L stick  fly    LB / RB or R stick  turn    A / B  climb / descend    RT guns    LT rockets    Y  get out"
                    : "WASD  fly    Q / E or mouse  turn    Space / Ctrl  climb / descend    LMB guns    RMB rockets    F  get out";
            }
            Place(flight, new Vector2(-halfW + 40f, -halfH + 30f), flight.text);
        }

        private void Mark(Vector3 at, Vector2 size, Vector3 eye)
        {
            float dist = Vector3.Distance(eye, at);
            if (dist > targetRange || !ToCanvas(at, size, out Vector2 p)) return;
            if (Mathf.Abs(p.x) > size.x * 0.5f || Mathf.Abs(p.y) > size.y * 0.5f) return;
            MarkedTargets++;
            float s = Mathf.Clamp(400f / Mathf.Max(5f, dist), 6f, 14f);
            lines.Diamond(p + new Vector2(0f, s * 2.2f), s, enemyColour, 2f);
        }
    }
}
