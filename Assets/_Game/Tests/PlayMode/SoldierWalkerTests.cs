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
    /// <summary>Walking soldiers: they patrol, give way, die with the right animation and come back on reset.</summary>
    public class SoldierWalkerTests
    {
        private SoldierInput input;
        private HitscanWeapon weapon;
        private ThirdPersonCamera rig;
        private TargetResetController reset;
        private PrototypeHUD hud;
        private Transform player;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            Time.captureFramerate = 60;
            SceneManager.LoadScene("ForestRange");
            yield return null;
            yield return null;
            input = Object.FindFirstObjectByType<SoldierInput>();
            player = input.transform;
            weapon = player.GetComponent<HitscanWeapon>();
            rig = Object.FindFirstObjectByType<ThirdPersonCamera>();
            reset = Object.FindFirstObjectByType<TargetResetController>();
            hud = Object.FindFirstObjectByType<PrototypeHUD>();
            input.ExternalControl = true;
            yield return Frames(10);
        }

        [TearDown]
        public void Reset() => Time.captureFramerate = 0;

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        private static IEnumerator Seconds(float s) => Frames(Mathf.RoundToInt(s * 60f));

        private static float Ground(Vector3 p) =>
            Terrain.activeTerrain.SampleHeight(p) + Terrain.activeTerrain.transform.position.y;

        private static WalkerNPC Walker(string name) => WalkerNPC.All.First(w => w.name == name);

        private static string State(WalkerNPC w)
        {
            var a = w.GetComponentInChildren<Animator>();
            var info = a.GetCurrentAnimatorStateInfo(0);
            if (a.IsInTransition(0)) info = a.GetNextAnimatorStateInfo(0);
            foreach (var s in new[] { "Locomotion", "DieFront", "DieBack" })
                if (info.IsName(s)) return s;
            return "?";
        }

        private IEnumerator Teleport(Vector3 pos, float yaw)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(new Vector3(pos.x, Ground(pos) + 0.05f, pos.z), Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Snap();
            yield return Frames(3);
        }

        private void LookAt(Vector3 point)
        {
            for (int i = 0; i < 3; i++)
            {
                rig.Snap();
                Vector3 d = (point - rig.transform.position).normalized;
                rig.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                rig.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            }
        }

        [UnityTest]
        public IEnumerator WalkersPatrolOnTheGround()
        {
            Assert.AreEqual(7, WalkerNPC.All.Count);
            var start = WalkerNPC.All.ToDictionary(w => w, w => w.transform.position);
            yield return Seconds(4f);
            foreach (var w in WalkerNPC.All)
            {
                float moved = Vector3.Distance(start[w], w.transform.position);
                Assert.Greater(moved, 1.5f, $"{w.name} isn't walking");
                Assert.That(w.transform.position.y, Is.EqualTo(Ground(w.transform.position)).Within(0.05f), $"{w.name} off the ground");
                Assert.AreEqual("Locomotion", State(w));
            }
        }

        [UnityTest]
        public IEnumerator ShotFromTheFrontPlaysDying()
        {
            var w = Walker("Walker_RangeSouth");
            var dir = -w.transform.forward; // the round flies towards its face
            for (int i = 0; i < 4; i++) w.ApplyDamage(new DamageInfo(25f, w.transform.position + Vector3.up, -dir, dir, null));
            yield return Frames(20);
            Assert.IsFalse(w.IsAlive);
            Assert.IsFalse(w.DiedFromBehind);
            Assert.AreEqual("DieFront", State(w));
            Assert.IsTrue(w.GetComponentsInChildren<Collider>().All(c => !c.enabled), "a body doesn't stop bullets");
            Vector3 at = w.transform.position;
            yield return Seconds(1.5f);
            Assert.Less(Vector3.Distance(at, w.transform.position), 0.01f, "the dead stay put");
        }

        [UnityTest]
        public IEnumerator ShotFromBehindPlaysTheBackDeath()
        {
            var w = Walker("Walker_RangeNorth");
            var dir = w.transform.forward; // travelling the way it faces: from behind
            for (int i = 0; i < 4; i++) w.ApplyDamage(new DamageInfo(25f, w.transform.position + Vector3.up, -dir, dir, null));
            yield return Frames(20);
            Assert.IsTrue(w.DiedFromBehind);
            Assert.AreEqual("DieBack", State(w));
        }

        [UnityTest]
        public IEnumerator ShootingAWalkerDropsItAndHeadshotsAreCritical()
        {
            var w = Walker("Walker_RangeSouth");
            // Stand 7 m north of it, inside the lane, and track its head.
            yield return Teleport(w.transform.position + new Vector3(0f, 0f, 7f), 180f);
            input.Aim = true;
            var head = w.GetComponentsInChildren<Hitbox>().First(h => h.Critical).transform;
            int crits = 0, hits = 0;
            weapon.HitDamageable += (_, __, r) =>
            {
                hits++;
                if (r.Critical) crits++;
            };
            yield return Seconds(0.6f);
            input.Fire = true;
            for (int i = 0; i < 240 && w.IsAlive; i++)
            {
                LookAt(head.GetComponent<SphereCollider>().bounds.center);
                yield return null;
            }
            input.Fire = false;
            input.Aim = false;
            Assert.IsFalse(w.IsAlive, "a burst at the head drops a walker");
            Assert.Greater(crits, 0, "head hits count as critical");
            Assert.LessOrEqual(hits, 4);
            Assert.AreEqual(1, reset.WalkersDown);
            Assert.Greater(hud.HitMarkersShown, 0);
            var fx = player.GetComponent<WeaponEffects>();
            Assert.Greater(fx.ActiveSplats, 0, "hits on a walker leave blood");
            if (System.Environment.GetEnvironmentVariable("FOREST_SOLDIER_SHOTS") == "1")
            {
                yield return Seconds(1.5f);
                string dir = Path.Combine(Application.dataPath, "../Logs/Screens");
                Directory.CreateDirectory(dir);
                Shot(dir, "w_06_blood_after_burst");
            }

            input.ResetTargetsRequested = true;
            yield return Frames(3);
            Assert.AreEqual(0, fx.ActiveSplats, "reset wipes the blood");
        }

        [UnityTest]
        public IEnumerator WalkersGiveWayToThePlayer()
        {
            var w = Walker("Walker_Yard");
            yield return Seconds(0.5f);
            yield return Teleport(w.transform.position + w.transform.forward * 1f, 0f);
            yield return Seconds(1f);
            Assert.Less(w.CurrentSpeed, 0.05f, "stops when you stand in its way");
            yield return Teleport(new Vector3(14f, 0f, 0f), 90f);
            yield return Seconds(1f);
            Assert.Greater(w.CurrentSpeed, 0.5f, "and carries on once you've moved");
        }

        [UnityTest]
        public IEnumerator ResetBringsWalkersBack()
        {
            foreach (var w in WalkerNPC.All.ToArray())
                w.ApplyDamage(new DamageInfo(1000f, w.transform.position, Vector3.up, -w.transform.forward, null));
            yield return Frames(10);
            Assert.AreEqual(WalkerNPC.All.Count, reset.WalkersDown);
            input.ResetTargetsRequested = true;
            yield return Frames(5);
            Assert.AreEqual(0, reset.WalkersDown);
            foreach (var w in WalkerNPC.All)
            {
                Assert.IsTrue(w.IsAlive);
                Assert.AreEqual("Locomotion", State(w), $"{w.name} back on its feet");
                Assert.IsTrue(w.GetComponentsInChildren<Collider>().All(c => c.enabled));
            }
            yield return Seconds(1f);
            Assert.IsTrue(WalkerNPC.All.All(w => w.CurrentSpeed > 0.3f), "walking again");
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
            var w = Walker("Walker_RangeSouth");
            yield return Teleport(w.transform.position + new Vector3(-3f, 0f, 6f), 150f);
            rig.enabled = false;
            Vector3 side = w.transform.position + w.transform.right * 4f + Vector3.up * 1.3f;
            rig.transform.SetPositionAndRotation(side, Quaternion.LookRotation(w.transform.position + Vector3.up * 0.9f - side));
            Shot(dir, "w_01_walking");
            var d = -w.transform.forward;
            w.ApplyDamage(new DamageInfo(1000f, w.transform.position, d, -d, null));
            yield return Seconds(0.9f);
            Shot(dir, "w_02_dying_front_mid");
            yield return Seconds(2.5f);
            Shot(dir, "w_03_dead_front");

            var b = Walker("Walker_RangeNorth");
            Vector3 side2 = b.transform.position + b.transform.right * 4f + Vector3.up * 1.3f;
            rig.transform.SetPositionAndRotation(side2, Quaternion.LookRotation(b.transform.position + Vector3.up * 0.9f - side2));
            b.ApplyDamage(new DamageInfo(1000f, b.transform.position, Vector3.up, b.transform.forward, null));
            yield return Seconds(1.2f);
            Shot(dir, "w_04_dying_back_mid");
            yield return Seconds(2.6f);
            Shot(dir, "w_05_dead_back");
            rig.enabled = true;
        }

        private void Shot(string dir, string name)
        {
            var cam = rig.Camera;
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
}
