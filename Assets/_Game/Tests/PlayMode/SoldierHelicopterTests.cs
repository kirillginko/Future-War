using System.Collections;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Destruction;
using ForestSoldier.NPC;
using ForestSoldier.Player;
using ForestSoldier.Vehicles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>The Little Bird: boarding, flight on the Base Helicopter Controller asset, guns and rockets.</summary>
    public class SoldierHelicopterTests
    {
        private SoldierInput input;
        private Transform player;
        private HelicopterPilot heli;
        private HelicopterWeapons weapons;
        private HelicopterCamera chase;
        private ThirdPersonCamera rig;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            Time.captureFramerate = 60;
            SceneManager.LoadScene("ForestRange");
            yield return null;
            yield return null;
            input = Object.FindFirstObjectByType<SoldierInput>();
            player = input.transform;
            heli = Object.FindFirstObjectByType<HelicopterPilot>();
            weapons = heli.GetComponent<HelicopterWeapons>();
            chase = Object.FindFirstObjectByType<HelicopterCamera>(FindObjectsInactive.Include);
            rig = Object.FindFirstObjectByType<ThirdPersonCamera>();
            input.ExternalControl = true;
            foreach (var w in WalkerNPC.All.ToArray()) w.gameObject.SetActive(false);
            yield return Frames(10);
        }

        [TearDown]
        public void Reset() => Time.captureFramerate = 0;

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        private static IEnumerator Seconds(float s) => Frames(Mathf.RoundToInt(s * 60f));

        private IEnumerator Board()
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.position = heli.transform.position + heli.transform.right * 3f + Vector3.up * 0.05f;
            cc.enabled = true;
            yield return Frames(3);
            Assert.IsTrue(heli.PlayerInRange);
            input.InteractRequested = true;
            yield return Frames(3);
            Assert.IsTrue(heli.Occupied);
        }

        /// <summary>Wait for the rotors to run up to flying speed.</summary>
        private IEnumerator SpinUp()
        {
            for (int i = 0; i < 400 && heli.RotorSpeed < 1f; i++) yield return null;
            Assert.AreEqual(1f, heli.RotorSpeed, 1e-4f, "rotors up to speed");
        }

        private IEnumerator TakeOff(float seconds)
        {
            yield return SpinUp();
            input.Ascend = true;
            yield return Seconds(seconds);
            input.Ascend = false;
        }

        [UnityTest]
        public IEnumerator ItIsBuiltOnTheBaseHelicopterController()
        {
            var hc = heli.GetComponent<HelicopterController>();
            Assert.IsNotNull(hc, "the asset's flight model");
            Assert.AreEqual(2, heli.GetComponentsInChildren<HeliRotorController>().Length, "main and tail rotor spinners");
            var panel = heli.GetComponentInChildren<ControlPanel>(true);
            Assert.IsNotNull(panel);
            Assert.IsFalse(panel.enabled, "its legacy-input panel stays off; the pilot raises its event");
            Assert.IsTrue(hc.IsOnGround || heli.Altitude < 0.5f, "parked on the pad");
            Assert.That(hc.EngineForce, Is.EqualTo(0f).Within(0.01f), "engine off until someone gets in");
            yield return null;
        }

        [UnityTest]
        public IEnumerator GetInAndOut()
        {
            yield return Board();
            Assert.IsFalse(player.GetComponent<CharacterController>().enabled);
            Assert.IsFalse(player.GetComponentsInChildren<Renderer>().Any(r => r.enabled), "the soldier is in the seat, out of sight");
            Assert.IsTrue(chase.Engaged && chase.enabled);
            Assert.IsFalse(rig.enabled, "the on-foot camera hands over");
            Assert.IsTrue(player.IsChildOf(heli.transform));

            input.InteractRequested = true;
            yield return Frames(3);
            Assert.IsFalse(heli.Occupied);
            Assert.IsFalse(player.IsChildOf(heli.transform));
            Assert.IsTrue(player.GetComponent<CharacterController>().enabled);
            Assert.IsTrue(rig.enabled && !chase.enabled);
            Assert.Greater(Vector3.Distance(player.position, heli.transform.position), 1.5f, "out beside the door");
            Assert.IsTrue(player.GetComponentsInChildren<Renderer>().Any(r => r.enabled));
        }

        [UnityTest]
        public IEnumerator TakesOffClimbsAndHovers()
        {
            yield return Board();
            yield return SpinUp();
            input.Ascend = true;
            yield return Seconds(0.5f);
            Assert.Less(heli.Body.linearVelocity.y, 4f, "the climb builds up: it has weight");
            yield return Seconds(2.5f);
            input.Ascend = false;
            float released = heli.Altitude;
            Assert.Greater(released, 8f, "Space / A climbs");

            // Let go: it carries on up a little, then holds.
            yield return Seconds(1.5f);
            float h1 = heli.Altitude;
            Assert.Greater(h1, released + 1.5f, $"it carries on up after you let go ({released:F1} → {h1:F1} m)");
            Vector2 lo = Vector2.positiveInfinity, hi = Vector2.negativeInfinity;
            for (int i = 0; i < 150; i++)
            {
                lo = Vector2.Min(lo, heli.Wobble);
                hi = Vector2.Max(hi, heli.Wobble);
                yield return null;
            }
            Assert.That(heli.Altitude, Is.EqualTo(h1).Within(4f), "then holds its height (give or take the air)");
            Assert.Less(heli.Speed, 7f, "and stays roughly put");
            Assert.Greater(Mathf.Max(hi.x - lo.x, hi.y - lo.y), 1f, $"but it's never still: the air rocks it ({hi - lo})");

            float h2 = heli.Altitude;
            input.Descend = true;
            yield return Seconds(0.5f);
            Assert.Greater(heli.Body.linearVelocity.y, -4f, "the sink builds up too");
            yield return Seconds(1.5f);
            input.Descend = false;
            Assert.Less(heli.Altitude, h2 - 5f, "Ctrl / B comes down");
        }

        [UnityTest]
        public IEnumerator RotorsRunUpSlowlyAndNeverSlowOnTheWayDown()
        {
            var hc = heli.GetComponent<HelicopterController>();
            Assert.AreEqual(0f, heli.RotorSpeed, "still while parked");
            yield return Board();
            input.Ascend = true; // impatient: no lift until the rotors are up
            yield return Seconds(0.5f);
            float early = hc.MainRotorController.RotarSpeed;
            yield return Seconds(1f);
            float later = hc.MainRotorController.RotarSpeed;
            Assert.Greater(later, early * 3f, $"slow at first ({early:F0}°/s), then faster ({later:F0}°/s)");
            Assert.Less(heli.RotorSpeed, 0.6f);
            Assert.Less(heli.Altitude, 0.3f, "not off the ground before the rotors are up");
            yield return SpinUp();
            float full = hc.MainRotorController.RotarSpeed;
            yield return Seconds(2.5f);
            input.Ascend = false;
            Assert.Greater(heli.Altitude, 6f, "then it lifts");

            input.Descend = true;
            float slowest = float.MaxValue;
            for (int i = 0; i < 90; i++)
            {
                slowest = Mathf.Min(slowest, hc.MainRotorController.RotarSpeed);
                yield return null;
            }
            input.Descend = false;
            Assert.AreEqual(full, slowest, 1f, "rotors keep their speed while descending");

            yield return Seconds(3f); // settle onto the ground
            input.Descend = true;
            yield return Seconds(3f);
            input.Descend = false;
            input.InteractRequested = true;
            yield return Frames(3);
            Assert.IsFalse(heli.Occupied);
            yield return Seconds(2f);
            Assert.That(heli.RotorSpeed, Is.InRange(0.3f, 0.95f), "out of the seat: the rotors run down slowly");
        }

        [UnityTest]
        public IEnumerator HeavyWithMomentum()
        {
            yield return Board();
            yield return TakeOff(7f); // well clear of the hills: this one flies a long way
            yield return Seconds(1.5f);

            // Pedals: the turn rate builds, then carries the nose on after you let go.
            float yaw0 = heli.transform.eulerAngles.y;
            input.Yaw = 1f; // E / RB
            yield return Seconds(0.25f);
            Assert.Less(Mathf.Abs(heli.YawRate), 50f, "the turn has weight: it builds up");
            yield return Seconds(1.25f);
            input.Yaw = 0f;
            float turned = Mathf.Abs(Mathf.DeltaAngle(yaw0, heli.transform.eulerAngles.y));
            Assert.Greater(turned, 60f, $"yaws {turned:F0} degrees in 1.5 s");
            float atRelease = heli.transform.eulerAngles.y;
            yield return Seconds(2f);
            float coast = Mathf.Abs(Mathf.DeltaAngle(atRelease, heli.transform.eulerAngles.y));
            Assert.Greater(coast, 15f, $"the nose swings on {coast:F0}° after you let go");
            Assert.Less(Mathf.Abs(heli.YawRate), 15f, "and then settles (but for the air)");

            // The map is only 300 m across: run down the long diagonal, away from the rim hills.
            Vector3 corner = new Vector3(-110f, heli.transform.position.y, -110f) - heli.transform.position;
            var face = Quaternion.Euler(0f, Mathf.Atan2(corner.x, corner.z) * Mathf.Rad2Deg, 0f);
            heli.Body.rotation = face;
            heli.transform.rotation = face;
            heli.Body.angularVelocity = Vector3.zero;
            yield return Seconds(1f);

            // Lean: it swings into the lean rather than snapping, and overshoots a touch on the way back.
            Vector3 start = heli.transform.position;
            Vector3 nose = Vector3.ProjectOnPlane(heli.transform.forward, Vector3.up).normalized;
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(0.2f);
            float early = heli.transform.eulerAngles.x.Wrap180();
            Assert.Less(early, 8f, $"the nose takes its time going down ({early:F1}° after 0.2 s)");
            float mid = 0f;
            for (int i = 0; i < 48; i++) { mid = heli.transform.eulerAngles.x.Wrap180(); yield return null; }
            Assert.Less(mid, 25f, $"pitch has weight: still tipping over after 1 s ({mid:F0}°)");
            yield return Seconds(1.5f);
            float lean = heli.transform.eulerAngles.x.Wrap180();
            Assert.Greater(lean, 20f, $"then leans well in ({lean:F0}° nose down after 2.5 s)");

            // Weight: a big lean, but it gathers way slowly, building over seconds to a sensible speed.
            Assert.Less(heli.Speed, 10f, $"gathers way slowly ({heli.Speed * 3.6f:F0} km/h after 2.5 s)");
            yield return Seconds(3.5f);
            float fast = heli.Speed;
            Assert.That(fast, Is.InRange(8f, 38f), $"forward flight reaches {fast * 3.6f:F0} km/h after 6 s");
            Assert.Greater(Vector3.Dot(heli.transform.position - start, nose), 25f, "along the nose");

            // Let go: the nose comes back up to level (the air still rocks it), and it coasts on.
            input.Move = Vector2.zero;
            float flare = 90f;
            for (int i = 0; i < 120; i++)
            {
                flare = Mathf.Min(flare, heli.transform.eulerAngles.x.Wrap180());
                yield return null;
            }
            Assert.Less(flare, 6f, $"the nose comes back up to level ({flare:F1}°; at {heli.transform.position}, {heli.Altitude:F1} m up, ground {heli.OnGround}, {fast:F1} m/s)");
            Assert.Greater(heli.Speed, fast * 0.7f, "momentum: it carries on when you let go");

            // Lean back to brake (hold it too long and it flies backwards).
            input.Move = new Vector2(0f, -1f);
            float braked = 0f;
            for (int i = 0; i < 360; i++)
            {
                if (Vector3.Dot(heli.Body.linearVelocity, nose) < fast * 0.3f) { braked = i / 60f; break; }
                yield return null;
            }
            input.Move = Vector2.zero;
            Assert.IsFalse(heli.OnGround, "still flying (it didn't stop by hitting something)");
            Assert.Greater(braked, 1f, $"leaning back brakes it, but it takes a while ({braked:F1} s)");
        }

        [UnityTest]
        public IEnumerator FlightHudAndTargeting()
        {
            var hud = Object.FindFirstObjectByType<HelicopterHUD>();
            var soldierHud = Object.FindFirstObjectByType<ForestSoldier.UI.PrototypeHUD>();
            Assert.AreEqual(0, hud.LineCount, "nothing drawn on foot");
            Assert.IsFalse(soldierHud.Hidden);

            yield return Board();
            yield return TakeOff(1.5f);
            yield return Frames(2);
            Assert.IsTrue(soldierHud.Hidden, "the soldier's HUD hides while flying");
            Assert.Greater(hud.LineCount, 80, "heading tape, ladder, boxes, reticles");
            Assert.IsTrue(hud.GunReticleOnScreen, "gun reticle");

            // Face the range and look down it: targets marked, the rocket marker on the ground.
            var target = DamageableTarget.All.First(t => t.name == "Target_20m");
            Vector3 to = target.transform.position - heli.transform.position;
            var face = Quaternion.Euler(0f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 0f);
            heli.Body.rotation = face;
            heli.transform.rotation = face;
            chase.Pitch = 22f;
            yield return Seconds(0.6f);
            Assert.Greater(hud.MarkedTargets, 2, "the range's targets are marked");
            Assert.IsTrue(hud.RocketMarkerOnScreen, "the rocket marker shows where they'd land");

            // Put the reticle on the 20 m target: it locks, with its range.
            var col = target.GetComponentsInChildren<Collider>();
            Bounds b = col[0].bounds;
            foreach (var c in col) b.Encapsulate(c.bounds);
            for (int i = 0; i < 90 && hud.LockedTarget != target.transform; i++)
            {
                Vector3 d = b.center - chase.transform.position;
                Vector3 f = chase.transform.forward;
                float yawErr = Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
                input.Yaw = Mathf.Clamp(yawErr / 4f, -1f, 1f);
                float pitch = -Mathf.Asin(d.normalized.y) * Mathf.Rad2Deg;
                chase.Pitch = Mathf.Clamp(chase.Pitch + (pitch - chase.transform.eulerAngles.x.Wrap180()) * 0.4f, -15f, 45f);
                yield return null;
            }
            input.Yaw = 0f;
            Assert.AreEqual(target.transform, hud.LockedTarget, "the target under the reticle is locked");

            input.InteractRequested = true;
            yield return Frames(3);
            Assert.IsFalse(soldierHud.Hidden, "out of the seat: the soldier's HUD is back");
            Assert.AreEqual(0, hud.LineCount, "and the flight HUD is gone");
        }

        [UnityTest]
        public IEnumerator TheAirMovesItAbout()
        {
            yield return Board();
            yield return TakeOff(3f);
            yield return Seconds(1f);
            Vector3 p0 = heli.transform.position;
            float y0 = heli.transform.eulerAngles.y;
            float minP = 99f, maxP = -99f, minR = 99f, maxR = -99f, pushed = 0f;
            for (int i = 0; i < 300; i++) // 5 s hands off
            {
                Vector3 off = heli.transform.position - p0;
                off.y = 0f;
                pushed = Mathf.Max(pushed, off.magnitude);
                float p = heli.transform.eulerAngles.x.Wrap180(), r = heli.transform.eulerAngles.z.Wrap180();
                minP = Mathf.Min(minP, p); maxP = Mathf.Max(maxP, p);
                minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r);
                yield return null;
            }
            Vector3 drift = heli.transform.position - p0;
            drift.y = 0f;
            Debug.Log($"[heli air] pitch {minP:F1}..{maxP:F1} roll {minR:F1}..{maxR:F1} drift {drift.magnitude:F2} m yaw {Mathf.DeltaAngle(y0, heli.transform.eulerAngles.y):F1}");
            Assert.Greater(Mathf.Max(maxP - minP, maxR - minR), 2.5f, "it rocks in the air");
            Assert.Less(Mathf.Max(maxP - minP, maxR - minR), 20f, "but it stays flyable");
            Assert.Greater(pushed, 0.5f, $"the air pushes it about ({pushed:F1} m at most)");
            Assert.Less(drift.magnitude, 25f, "without carrying it off");
        }

        [UnityTest]
        public IEnumerator ControllerBindings()
        {
            var asset = (InputActionAsset)typeof(SoldierInput)
                .GetField("actions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(input);
            var map = asset.FindActionMap("Player", true);
            bool Bound(string action, string path) => map.FindAction(action, true).bindings.Any(b => b.path == path);
            Assert.IsTrue(Bound("Interact", "<Gamepad>/buttonNorth"), "Y gets in and out");
            Assert.IsTrue(Bound("Interact", "<Gamepad>/dpad/up"));
            Assert.IsTrue(Bound("Yaw", "<Gamepad>/leftShoulder") && Bound("Yaw", "<Gamepad>/rightShoulder"), "LB / RB turn");
            Assert.IsTrue(Bound("Yaw", "<Keyboard>/q") && Bound("Yaw", "<Keyboard>/e"), "Q / E turn");
            Assert.IsTrue(Bound("Ascend", "<Gamepad>/buttonSouth") && Bound("Descend", "<Gamepad>/buttonEast"), "A / B climb and descend");
            Assert.IsTrue(Bound("Fire", "<Gamepad>/rightTrigger") && Bound("Aim", "<Gamepad>/leftTrigger"), "RT guns, LT rockets");
            Assert.IsTrue(Bound("Move", "<Gamepad>/leftStick") && Bound("LookStick", "<Gamepad>/rightStick"));

            // The right stick turns too, and the HUD speaks controller.
            yield return Board();
            yield return TakeOff(2f);
            input.UsingGamepad = true;
            float yaw0 = heli.transform.eulerAngles.y;
            input.LookStick = new Vector2(-1f, 0f);
            yield return Seconds(1.5f);
            input.LookStick = Vector2.zero;
            Assert.Greater(Mathf.Abs(Mathf.DeltaAngle(yaw0, heli.transform.eulerAngles.y)), 45f, "right stick X turns");
            var hud = Object.FindFirstObjectByType<HelicopterHUD>();
            yield return null;
            StringAssert.Contains("LB / RB", hud.FlightText);
            input.UsingGamepad = false;
        }

        [UnityTest]
        public IEnumerator MinigunsHitWhatTheCrosshairIsOn()
        {
            yield return Board();
            yield return TakeOff(1.5f);
            var target = DamageableTarget.All.First(t => t.name == "Target_20m");
            // Point the nose and the camera at the target.
            Vector3 to = target.transform.position + Vector3.up - heli.transform.position;
            var face = Quaternion.Euler(0f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 0f);
            heli.Body.rotation = face; // the body, or interpolation puts the old heading back
            heli.transform.rotation = face;
            heli.Body.angularVelocity = Vector3.zero;
            // The middle of what can be hit (the board, not the post).
            var boards = target.GetComponentsInChildren<Collider>();
            Bounds hitBox = boards[0].bounds;
            foreach (var c in boards) hitBox.Encapsulate(c.bounds);
            Vector3 aim = hitBox.center;
            int shots = weapons.ShotsFired;
            int hits = 0;
            weapons.HitDamageable += (_, t, __) => { if (ReferenceEquals(t, target)) hits++; };
            // Fly it like a player: the air keeps nudging the nose, so keep steering on with
            // the pedals and the camera, and open fire once on target.
            float lastErr = 0f;
            for (int i = 0; i < 600 && target.IsAlive; i++)
            {
                // Steer the crosshair (the camera's view), which trails the nose a little.
                Vector3 d = aim - chase.transform.position;
                Vector3 f = chase.transform.forward;
                float yawErr = Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
                // Track it: the gusts drift the helicopter, so the bearing keeps moving. Ease off as
                // the error closes (the turn has weight and carries on).
                float closing = i == 0 ? 0f : (yawErr - lastErr) / Time.deltaTime;
                lastErr = yawErr;
                input.Yaw = Mathf.Clamp((yawErr + closing * 0.35f) / 4f, -1f, 1f);
                float pitch = -Mathf.Asin(d.normalized.y) * Mathf.Rad2Deg;
                float pitchErr = pitch - chase.transform.eulerAngles.x.Wrap180();
                chase.Pitch = Mathf.Clamp(chase.Pitch + pitchErr * 0.4f, -15f, 45f);
                input.Fire = i > 60 && Mathf.Abs(yawErr) < 1.5f && Mathf.Abs(pitchErr) < 1.5f;
                yield return null;
            }
            input.Yaw = 0f;
            input.Fire = false;
            Assert.Greater(weapons.ShotsFired - shots, 5, "the guns spin up and fire");
            Assert.IsFalse(target.IsAlive, $"and cut the target down ({hits} hits, aim {weapons.AimPoint}, board {hitBox.center} size {hitBox.size})");
        }

        [UnityTest]
        public IEnumerator RocketsRippleExplodeAndRearmOnTheGround()
        {
            yield return Board();
            yield return TakeOff(1.5f);
            chase.Pitch = 30f; // at the ground ahead
            yield return Frames(20);
            int bursts = 0;
            weapons.RocketExploded += (_, __) => bursts++;
            input.Aim = true;
            yield return Seconds(0.6f);
            input.Aim = false;
            int fired = weapons.RocketsFired;
            Assert.GreaterOrEqual(fired, 3, "held: a ripple of rockets");
            Assert.AreEqual(weapons.RocketCapacity - fired, weapons.Rockets);
            yield return Seconds(1.5f);
            Assert.GreaterOrEqual(bursts, fired, "each one goes off");
            Assert.Greater(weapons.Debris.Thrown, fired * 5, "each one tears chunks out of the ground");
            Assert.Greater(weapons.Debris.Active, 0);

            // Hold descend until it's down, as a player would.
            input.Descend = true;
            float touchdown = 0f;
            for (int i = 0; i < 480 && !heli.OnGround; i++)
            {
                if (heli.Altitude < 1f) touchdown = Mathf.Min(touchdown, heli.Body.linearVelocity.y);
                yield return null;
            }
            yield return Frames(10);
            input.Descend = false;
            Assert.Greater(touchdown, -4f, $"it eases off before the ground ({touchdown:F1} m/s at the skids)");
            string trace = "";
            for (int i = 0; i < 60; i++)
            {
                if (i % 10 == 0) trace += $" [{i}: g {heli.OnGround} alt {heli.Altitude:F2} vy {heli.Body.linearVelocity.y:F2} eng {heli.EngineForce:F1}]";
                yield return null;
            }
            Assert.IsTrue(heli.OnGround, "landed" + trace);
            for (int i = 0; i < 240 && weapons.Rockets < weapons.RocketCapacity; i++) yield return null;
            Assert.AreEqual(weapons.RocketCapacity, weapons.Rockets,
                $"pods refilled on the ground (on ground {heli.OnGround}, {heli.Altitude:F2} m up, {heli.Speed:F2} m/s)");
        }

        [UnityTest]
        public IEnumerator RocketsBreachWiderThanAGrenade()
        {
            var house = DestructibleStructure.All.OrderByDescending(h => h.FragmentCount).First();
            // A point on the outside of a ground-floor wall fragment.
            int wall = Enumerable.Range(0, house.FragmentCount)
                .Where(i => house.Fragment(i).position.y > house.transform.position.y + 0.8f)
                .OrderBy(i => house.Fragment(i).position.y).First();
            Vector3 at = house.Fragment(wall).position + (house.Fragment(wall).position - house.transform.position).normalized * 0.6f;
            at.y = house.Fragment(wall).position.y;
            int mask = Physics.DefaultRaycastLayers;
            var grenadeStyle = Explosions.Explode(at, 6f, 200f, 13f, mask, null);
            yield return Frames(2);
            DestructibleStructure.RebuildAll();
            yield return Frames(2);
            var rocket = Explosions.Explode(at, 6f, 200f, 13f, mask, null, breach: 1.7f);
            Assert.Greater(rocket.FragmentsBroken, grenadeStyle.FragmentsBroken,
                $"a rocket breaks more of the wall ({rocket.FragmentsBroken} pieces against {grenadeStyle.FragmentsBroken})");
            yield return Frames(2);
            DestructibleStructure.RebuildAll();
        }

        [UnityTest]
        public IEnumerator GunsFireOnTheLockedTarget()
        {
            yield return Board();
            yield return TakeOff(1.5f);
            var target = DamageableTarget.All.First(t => t.name == "Target_20m");
            // Just this one on the range: the others sit within a few degrees of it from up here.
            foreach (var other in DamageableTarget.All.ToArray())
                if (other != target) other.gameObject.SetActive(false);
            Vector3 aim = HelicopterWeapons.TargetPoint(target.transform);
            Vector3 to = aim - heli.transform.position;
            var face = Quaternion.Euler(0f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 0f);
            heli.Body.rotation = face;
            heli.transform.rotation = face;

            // Put the crosshair on it: it locks.
            for (int i = 0; i < 120 && weapons.LockedTarget != target.transform; i++)
            {
                SteerCrosshairAt(aim, 0f);
                yield return null;
            }
            Assert.AreEqual(target.transform, weapons.LockedTarget, "the target under the crosshair locks");

            // Look 4° above it (keeping the nose on its bearing) and fire: the guns go for the lock.
            int hitsOnTarget = 0;
            // A hit lands on the target or one of its hit zones (torso, head).
            weapons.HitDamageable += (_, t, __) => { if (t is Component c && c.transform.IsChildOf(target.transform)) hitsOnTarget++; };
            for (int i = 0; i < 300 && target.IsAlive; i++)
            {
                SteerCrosshairAt(aim, -4f);
                input.Fire = i > 20;
                yield return null;
            }
            input.Fire = false;
            input.Yaw = 0f;
            Assert.Greater(hitsOnTarget, 3, $"rounds land on the locked target though the crosshair is off it (lock lost: {weapons.LockLostBecause})");
            Assert.IsFalse(target.IsAlive, "and cut it down");
        }

        private float steerLastErr;
        private int steerFrame = -1;

        /// <summary>
        /// Pedals and camera pitch to put the crosshair <paramref name="pitchOffset"/> degrees above
        /// <paramref name="point"/>, like a player: easing off as the error closes, since the turn has weight.
        /// </summary>
        private void SteerCrosshairAt(Vector3 point, float pitchOffset)
        {
            Vector3 d = point - chase.transform.position;
            Vector3 f = chase.transform.forward;
            float yawErr = Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            float closing = steerFrame == Time.frameCount - 1 ? (yawErr - steerLastErr) / Time.deltaTime : 0f;
            steerLastErr = yawErr;
            steerFrame = Time.frameCount;
            input.Yaw = Mathf.Clamp((yawErr + closing * 0.35f) / 4f, -1f, 1f);
            float pitch = -Mathf.Asin(d.normalized.y) * Mathf.Rad2Deg + pitchOffset;
            chase.Pitch = Mathf.Clamp(chase.Pitch + (pitch - chase.transform.eulerAngles.x.Wrap180()) * 0.4f, -15f, 45f);
        }

        [UnityTest]
        public IEnumerator Screenshots()
        {
            if (System.Environment.GetEnvironmentVariable("FOREST_SOLDIER_SHOTS") != "1")
            {
                Assert.Ignore("Set FOREST_SOLDIER_SHOTS=1 to capture screenshots.");
                yield break;
            }
            string dir = Path.Combine(Application.dataPath, "../Logs/Screens");
            Directory.CreateDirectory(dir);
            var cam = rig.GetComponent<Camera>();
            rig.enabled = false;
            Vector3 eye = heli.transform.position + heli.transform.right * 9f + heli.transform.forward * 6f + Vector3.up * 3f;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(heli.transform.position + Vector3.up * 1.4f - eye));
            Shot(cam, dir, "h_01_parked");
            rig.enabled = true;

            yield return Board();
            yield return TakeOff(2.5f);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(1.5f);
            Shot(cam, dir, "h_02_flying");
            input.Move = Vector2.zero;
            chase.Pitch = 22f;
            yield return Seconds(1.5f);
            input.Fire = true;
            yield return Seconds(0.6f);
            Shot(cam, dir, "h_03_miniguns");
            input.Fire = false;
            input.Aim = true;
            yield return Seconds(0.5f);
            input.Aim = false;
            yield return Seconds(0.5f);
            Shot(cam, dir, "h_04_rockets");
            yield return Seconds(0.6f);
            Shot(cam, dir, "h_05_impacts");
        }

        private static void Shot(Camera cam, string dir, string name)
        {
            var rt = new RenderTexture(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            cam.targetTexture = prev;
            RenderTexture.active = null;
            Object.Destroy(tex);
            rt.Release();
        }
    }

    internal static class AngleExt
    {
        public static float Wrap180(this float a) => Mathf.DeltaAngle(0f, a);
    }
}
