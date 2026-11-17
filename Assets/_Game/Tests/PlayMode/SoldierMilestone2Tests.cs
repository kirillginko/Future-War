using System.Collections;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.NPC;
using ForestSoldier.Player;
using ForestSoldier.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>
    /// Shooting range: hit resolution, firing, magazine and reload, targets,
    /// reset, and the barrel following the crosshair. Same harness as
    /// Milestone 1 (ExternalControl input, fixed 60 fps).
    /// </summary>
    public class SoldierMilestone2Tests
    {
        private SoldierInput input;
        private SoldierMotor motor;
        private HitscanWeapon weapon;
        private ThirdPersonCamera rig;
        private PrototypeHUD hud;
        private TargetResetController targets;
        private Transform player;

        private const int Mask = ~(1 << 8) & ~(1 << 2);

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            Time.captureFramerate = 60;
            SceneManager.LoadScene("ForestRange");
            yield return null;
            yield return null;
            input = Object.FindFirstObjectByType<SoldierInput>();
            player = input.transform;
            motor = player.GetComponent<SoldierMotor>();
            weapon = player.GetComponent<HitscanWeapon>();
            rig = Object.FindFirstObjectByType<ThirdPersonCamera>();
            hud = Object.FindFirstObjectByType<PrototypeHUD>();
            targets = Object.FindFirstObjectByType<TargetResetController>();
            input.ExternalControl = true;
            // Walkers have their own tests; keep them out of these bullet paths.
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

        private IEnumerator Teleport(Vector3 pos, float yaw)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Pitch = 0f;
            rig.Snap();
            yield return Frames(5);
        }

        /// <summary>Point the screen centre at a world position (the camera orbits, so iterate).</summary>
        private void LookAt(Vector3 point)
        {
            for (int i = 0; i < 4; i++)
            {
                rig.Snap();
                Vector3 d = (point - rig.transform.position).normalized;
                rig.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                rig.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            }
            rig.Snap();
        }

        private static DamageableTarget Target(string name) =>
            DamageableTarget.All.First(t => t.name == name);

        private static GameObject Box(Vector3 pos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.position = pos;
            go.transform.localScale = size;
            return go;
        }

        // ------------------------------------------------------------ resolver

        [UnityTest]
        public IEnumerator Resolver_HitsWhatTheCrosshairIsOn()
        {
            var o = new Vector3(500f, 50f, 0f);
            var target = Box(o + new Vector3(0f, 1.8f, 10f), new Vector3(2f, 1f, 0.2f));
            Physics.SyncTransforms();
            var cam = new Ray(o + new Vector3(0.6f, 1.9f, -3f), Vector3.forward);
            var shot = HitscanResolver.Resolve(cam, o + new Vector3(0.3f, 1.5f, 0.7f), o + new Vector3(0f, 1.45f, 0f), 100f, Mask);
            Assert.IsTrue(shot.Hit);
            Assert.AreEqual(target.GetComponent<Collider>(), shot.Info.collider);
            Assert.IsFalse(shot.Obstructed);
            Object.Destroy(target);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Resolver_LowCoverTakesTheRoundEvenIfTheCameraSeesOverIt()
        {
            var o = new Vector3(500f, 50f, 50f);
            var target = Box(o + new Vector3(0f, 1.9f, 12f), new Vector3(1f, 0.6f, 0.2f));
            var cover = Box(o + new Vector3(0f, 0.8f, 2f), new Vector3(3f, 1.6f, 0.3f)); // top at 1.6
            Physics.SyncTransforms();
            Vector3 camPos = o + new Vector3(0f, 1.9f, -2.5f);
            var cam = new Ray(camPos, (target.transform.position - camPos).normalized);
            Assert.IsFalse(Physics.Linecast(camPos, target.transform.position - Vector3.forward * 0.2f, out _, Mask),
                "setup: the camera must see the target over the cover");
            var shot = HitscanResolver.Resolve(cam, o + new Vector3(0f, 1.35f, 0.7f), o + new Vector3(0f, 1.45f, 0f), 100f, Mask);
            Assert.IsTrue(shot.Hit);
            Assert.AreEqual(cover.GetComponent<Collider>(), shot.Info.collider);
            Assert.IsTrue(shot.Obstructed);
            Object.Destroy(target);
            Object.Destroy(cover);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Resolver_BarrelThroughAWallHitsTheWall()
        {
            var o = new Vector3(500f, 50f, 100f);
            var wall = Box(o + new Vector3(0f, 1.5f, 0.4f), new Vector3(3f, 3f, 0.1f));
            var target = Box(o + new Vector3(0f, 1.5f, 10f), new Vector3(1f, 1f, 0.2f));
            Physics.SyncTransforms();
            var cam = new Ray(o + new Vector3(0f, 1.6f, 0.2f), Vector3.forward); // camera pulled in to the wall
            var shot = HitscanResolver.Resolve(cam, o + new Vector3(0f, 1.4f, 0.75f), o + new Vector3(0f, 1.45f, 0f), 100f, Mask);
            Assert.AreEqual(wall.GetComponent<Collider>(), shot.Info.collider);
            Object.Destroy(wall);
            Object.Destroy(target);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Resolver_IgnoresThingsBetweenCameraAndShooter()
        {
            var o = new Vector3(500f, 50f, 150f);
            var behind = Box(o + new Vector3(0f, 1.8f, -1.5f), new Vector3(2f, 2f, 0.1f));
            var target = Box(o + new Vector3(0f, 1.8f, 10f), new Vector3(1f, 1f, 0.2f));
            Physics.SyncTransforms();
            var cam = new Ray(o + new Vector3(0f, 1.8f, -3f), Vector3.forward);
            var shot = HitscanResolver.Resolve(cam, o + new Vector3(0f, 1.6f, 0.7f), o + new Vector3(0f, 1.45f, 0f), 100f, Mask);
            Assert.AreEqual(target.GetComponent<Collider>(), shot.Info.collider);
            Object.Destroy(behind);
            Object.Destroy(target);
            yield return null;
        }

        // ------------------------------------------------------------ in the range

        [UnityTest]
        public IEnumerator AimedBurstDropsTheFifteenMetreTarget()
        {
            var t = Target("Target_15m");
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            input.Aim = true;
            LookAt(t.transform.position + Vector3.up * 1.0f);
            yield return Seconds(0.5f);
            int hits = 0;
            weapon.HitDamageable += (_, __, ___) => hits++;
            int before = weapon.Ammo;
            input.Fire = true;
            for (int i = 0; i < 180 && t.IsAlive; i++) yield return null;
            input.Fire = false;
            Assert.IsFalse(t.IsAlive, "four torso hits (or two to the head) should drop it");
            Assert.GreaterOrEqual(hits, 2);
            int spent = before - weapon.Ammo;
            Assert.LessOrEqual(spent, 8, "aimed fire at 15 m should rarely miss");
            Assert.Greater(hud.HitMarkersShown, 0);
            Assert.AreEqual(1, targets.Down);
            yield return Seconds(0.5f);
            Assert.Greater(Vector3.Angle(t.transform.Find("Hinge").up, Vector3.up), 60f, "dummy falls over");
        }

        [UnityTest]
        public IEnumerator FireRateMagazineAndReload()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = -40f; // at the sky
            yield return Frames(30);
            int shots = 0;
            weapon.Fired += _ => shots++;
            int shotsWhenReloadStarted = -1;
            weapon.ReloadStarted += () => shotsWhenReloadStarted = shots;
            input.Fire = true;
            yield return Seconds(1f);
            Assert.That(shots, Is.InRange(9, 11), "600 rpm");
            for (int i = 0; i < 180 && !weapon.IsReloading; i++) yield return null;
            Assert.IsTrue(weapon.IsReloading, "the last round starts the reload");
            Assert.AreEqual(weapon.MagazineSize, shotsWhenReloadStarted);
            Assert.AreEqual(weapon.MagazineSize, shots);
            Assert.AreEqual(0, weapon.Ammo);
            Assert.IsTrue(player.GetComponent<UpperBodyOverlay>().IsPlaying, "reload clip plays");
            yield return Seconds(0.5f);
            Assert.AreEqual(weapon.MagazineSize, shots, "no shots while reloading");
            for (int i = 0; i < 180 && shots == weapon.MagazineSize; i++) yield return null;
            input.Fire = false;
            Assert.IsFalse(weapon.IsReloading);
            Assert.AreEqual(weapon.MagazineSize + 1, shots, "held fire resumes after reloading");
            Assert.AreEqual(weapon.MagazineSize - 1, weapon.Ammo);
        }

        [UnityTest]
        public IEnumerator ManualReloadStillWorksWithRoundsRemaining()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = -40f;
            input.Fire = true;
            for (int i = 0; i < 30 && weapon.ShotsFired == 0; i++) yield return null;
            input.Fire = false;
            Assert.That(weapon.Ammo, Is.InRange(1, weapon.MagazineSize - 1));
            Assert.IsFalse(weapon.IsReloading);
            input.ReloadRequested = true;
            yield return Frames(2);
            Assert.IsTrue(weapon.IsReloading);
            yield return Seconds(2.3f);
            Assert.IsFalse(weapon.IsReloading);
            Assert.AreEqual(weapon.MagazineSize, weapon.Ammo);
        }

        [UnityTest]
        public IEnumerator ResetBringsEveryTargetBack()
        {
            var t = Target("Target_10m");
            t.ApplyDamage(new DamageInfo(1000f, t.transform.position, Vector3.up, Vector3.right, null));
            yield return Seconds(0.5f);
            Assert.IsFalse(t.IsAlive);
            Assert.IsTrue(t.GetComponentsInChildren<Collider>().All(c => !c.enabled), "downed targets stop bullets no more");
            Assert.AreEqual(1, targets.Down);

            input.ResetTargetsRequested = true;
            yield return Frames(2);
            Assert.IsFalse(input.ResetTargetsRequested, "request consumed");
            Assert.AreEqual(0, targets.Down);
            Assert.IsTrue(t.IsAlive);
            Assert.That(t.Health, Is.EqualTo(t.MaxHealth));
            Assert.IsTrue(t.GetComponentsInChildren<Collider>().All(c => c.enabled));
            Assert.AreEqual(9, targets.Total);
        }

        [UnityTest]
        public IEnumerator BarrelFollowsTheCrosshairUpAndDown()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            input.Aim = true;
            foreach (float pitch in new[] { -30f, 0f, 20f })
            {
                rig.Pitch = pitch;
                yield return Seconds(0.8f);
                Vector3 want = weapon.AimPoint - weapon.Muzzle.position;
                float err = Vector3.Angle(weapon.Muzzle.forward, want);
                Assert.Less(err, 3f, $"barrel off the crosshair by {err:F1} deg at camera pitch {pitch}: " +
                    $"barrel {weapon.Muzzle.forward} at {weapon.Muzzle.position}, aim point {weapon.AimPoint}, " +
                    $"rig weight {player.GetComponent<SoldierAimRig>().Weight:F2}, body yaw {player.eulerAngles.y:F1}");
                var upright = Vector3.ProjectOnPlane(Vector3.up, weapon.Muzzle.forward);
                Assert.Less(Vector3.Angle(weapon.Muzzle.up, upright), 3f, $"rifle cant at camera pitch {pitch}");
            }
        }

        [UnityTest]
        public IEnumerator FirstShotUsesTheRaisedRifleInBothStances()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            var head = player.GetComponent<SoldierAnimator>().Animator.GetComponentsInChildren<Transform>()
                .First(bone => bone.name == "head");
            float standingHeadHeight = head.position.y;
            foreach (bool crouch in new[] { false, true })
            {
                input.Crouch = crouch;
                input.Aim = crouch;
                rig.Pitch = 0f;
                yield return Frames(24);
                Assert.AreEqual(crouch, motor.IsCrouching);
                if (crouch) Assert.Less(head.position.y, standingHeadHeight - 0.35f);

                float sightError = float.NaN;
                float cant = float.NaN;
                void CheckShot(ShotResult shot)
                {
                    var muzzle = weapon.Muzzle;
                    Vector3 eye = head.position + player.rotation * new Vector3(0.035f, 0.08f, 0.07f);
                    Vector3 sight = muzzle.position + muzzle.up * 0.075f;
                    sightError = Vector3.ProjectOnPlane(eye - sight, muzzle.forward).magnitude;
                    cant = Vector3.Angle(muzzle.up, Vector3.ProjectOnPlane(Vector3.up, muzzle.forward));
                }
                weapon.Fired += CheckShot;
                input.Fire = true;
                for (int i = 0; i < 12 && float.IsNaN(sightError); i++) yield return null;
                input.Fire = false;
                weapon.Fired -= CheckShot;
                Assert.Less(sightError, 0.055f, $"sight distance from the right eye in crouch={crouch}");
                Assert.Less(cant, 3f, $"rifle cant in crouch={crouch}");
            }
        }

        [UnityTest]
        public IEnumerator HipFireTurnsToTheCameraBeforeShooting()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 0f);
            rig.Yaw = 90f; // camera looks down range, body faces north
            int shots = 0;
            float firstShotError = -1f;
            weapon.Fired += _ =>
            {
                if (shots++ == 0) firstShotError = motor.FacingError;
            };
            input.Fire = true;
            yield return Seconds(0.5f);
            input.Fire = false;
            Assert.Greater(shots, 0);
            Assert.LessOrEqual(firstShotError, 25f, "no shots out of the side of the body");
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
            var t20 = Target("Target_20m");
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            yield return Seconds(0.4f);
            Shot(dir, "m2_01_range_hip");

            input.Aim = true;
            LookAt(t20.transform.position + Vector3.up * 1.0f);
            yield return Seconds(0.6f);
            Shot(dir, "m2_02_aim_20m");

            input.Fire = true;
            for (int i = 0; i < 40; i++)
            {
                yield return null;
                if (weapon.ShotsFired == 2)
                {
                    Shot(dir, "m2_03_firing");
                    break;
                }
            }
            for (int i = 0; i < 120 && t20.IsAlive; i++) yield return null;
            input.Fire = false;
            yield return Frames(2);
            Shot(dir, "m2_04_hit_marker");
            yield return Seconds(0.5f);
            Shot(dir, "m2_05_target_down");

            input.Aim = false;
            input.ReloadRequested = true;
            yield return Seconds(0.9f);
            rig.enabled = false;
            rig.transform.SetPositionAndRotation(player.position + player.right * 2.6f + Vector3.up * 1.4f,
                Quaternion.LookRotation(-player.right));
            Shot(dir, "m2_06_reload_side");
            rig.enabled = true;
            yield return Seconds(1.6f);

            // side view while aiming up and firing
            input.Aim = true;
            rig.Pitch = -20f;
            input.Fire = true;
            yield return Seconds(0.3f);
            rig.enabled = false;
            rig.transform.SetPositionAndRotation(player.position + player.right * 3f + Vector3.up * 1.5f,
                Quaternion.LookRotation(-player.right));
            Shot(dir, "m2_07_aim_up_side");
            rig.enabled = true;
            input.Fire = false;
        }

        private void Shot(string dir, string name)
        {
            var cam = rig.Camera;
            var rt = new RenderTexture(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            Canvas.ForceUpdateCanvases();
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
}
