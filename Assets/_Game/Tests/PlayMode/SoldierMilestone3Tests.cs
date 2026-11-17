using System.Collections;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Environment;
using ForestSoldier.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>The forest: flat clearings, walkable trail, trees and rocks that block, a clear range, a closed boundary.</summary>
    public class SoldierMilestone3Tests
    {
        private SoldierInput input;
        private SoldierMotor motor;
        private HitscanWeapon weapon;
        private ThirdPersonCamera rig;
        private Transform player;
        private Terrain terrain;

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
            terrain = Terrain.activeTerrain;
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

        private float Ground(float x, float z) => terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;

        private IEnumerator Teleport(Vector2 at, float yaw)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(new Vector3(at.x, Ground(at.x, at.y) + 0.05f, at.y), Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Pitch = 10f;
            rig.Snap();
            yield return Frames(5);
        }

        private static Transform[] Children(string path) =>
            GameObject.Find(path).transform.Cast<Transform>().ToArray();

        // ------------------------------------------------------------ ground

        [UnityTest]
        public IEnumerator ClearingsAreFlatAndTheForestRolls()
        {
            Assert.IsNotNull(terrain, "a Terrain");
            foreach (var p in new[] { new Vector2(0f, 0f), new Vector2(-25f, -30f), new Vector2(25f, 25f), new Vector2(60f, 0f), new Vector2(105f, 10f) })
                Assert.That(Ground(p.x, p.y), Is.EqualTo(0f).Within(0.05f), $"clearing ground at {p}");

            var forest = Enumerable.Range(0, 40).Select(i => Ground(-60f + (i % 8) * 6f, 50f + (i / 8) * 6f)).ToArray();
            Assert.Greater(forest.Max() - forest.Min(), 1f, "forest floor should roll");
            Assert.IsTrue(motor.IsGrounded, "spawn stands on the ground");
            Assert.That(player.position.y, Is.EqualTo(0f).Within(0.1f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheTrailIsWalkable()
        {
            // Calibrate: how far 2 s of default movement gets on flat, open ground.
            yield return Teleport(new Vector2(-20f, -25f), 0f);
            Vector3 flatStart = player.position;
            input.Move = new Vector2(0f, 1f);
            yield return Frames(120);
            input.Move = Vector2.zero;
            float flat = Vector3.Distance(flatStart, player.position);
            Assert.Greater(flat, 0.5f, "calibration walk went nowhere");

            for (float t = 0.05f; t < 0.95f; t += 0.15f)
            {
                Vector2 a = ForestLayout.TrailPoint(t), b = ForestLayout.TrailPoint(t + 0.02f);
                float yaw = Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg;
                yield return Teleport(a, yaw);
                Vector3 start = player.position;
                input.Move = new Vector2(0f, 1f);
                float minGroundGap = float.MaxValue;
                for (int i = 0; i < 120; i++)
                {
                    // steer along the trail
                    float u = Mathf.Min(1f, t + 0.02f + i / 120f * 0.015f);
                    Vector2 next = ForestLayout.TrailPoint(u + 0.01f);
                    rig.Yaw = Mathf.Atan2(next.x - player.position.x, next.y - player.position.z) * Mathf.Rad2Deg;
                    yield return null;
                    minGroundGap = Mathf.Min(minGroundGap, player.position.y - Ground(player.position.x, player.position.z));
                }
                input.Move = Vector2.zero;
                float moved = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(player.position.x, player.position.z));
                Assert.Greater(moved, flat * 0.7f, $"slowed or stuck on the trail near {a} (t={t:F2}): {moved:F2} m vs {flat:F2} m on flat ground");
                Assert.Greater(minGroundGap, -0.2f, $"fell into the ground near {a}");
                Assert.IsTrue(motor.IsGrounded, $"airborne on the trail near {a}");
            }
        }

        [UnityTest]
        public IEnumerator TreesAndRocksSitOnTheGround()
        {
            foreach (var group in new[] { "Forest/Trees", "Forest/Rocks", "Forest/Bushes" })
            {
                var items = Children(group);
                Assert.Greater(items.Length, 10, $"{group} populated");
                foreach (var t in items)
                {
                    float g = Ground(t.position.x, t.position.z);
                    Assert.That(t.position.y, Is.EqualTo(g).Within(0.8f), $"{t.name} at {t.position} floats or sinks (ground {g:F2})");
                }
            }
            yield return null;
        }

        // ------------------------------------------------------------ blocking

        private Transform TreeOnFlatGround()
        {
            foreach (var t in Children("Forest/Trees"))
            {
                var p = new Vector2(t.position.x, t.position.z);
                if (ForestLayout.DistanceToClearing(p) > 30f) continue;
                // 3 m south must be free and roughly level
                var from = p + Vector2.down * 3f;
                if (Mathf.Abs(Ground(from.x, from.y) - Ground(p.x, p.y)) > 0.4f) continue;
                var mid = new Vector3(from.x, Ground(from.x, from.y) + 1f, from.y);
                if (Physics.OverlapSphere(mid, 0.6f).Any(c => !(c is TerrainCollider))) continue;
                if (Physics.Linecast(mid, new Vector3(p.x, mid.y, p.y), out var hit) && hit.transform != t) continue;
                return t;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator TreesStopThePlayer()
        {
            var tree = TreeOnFlatGround();
            Assert.IsNotNull(tree, "a tree with clear, level ground to its south");
            var p = new Vector2(tree.position.x, tree.position.z);
            yield return Teleport(p + Vector2.down * 3f, 0f);
            input.Move = new Vector2(0f, 1f);
            float closest = float.MaxValue;
            for (int i = 0; i < 180; i++)
            {
                yield return null;
                closest = Mathf.Min(closest, Vector2.Distance(p, new Vector2(player.position.x, player.position.z)));
            }
            input.Move = Vector2.zero;
            var trunk = tree.GetComponent<CapsuleCollider>();
            float trunkRadius = trunk.radius * Mathf.Max(tree.lossyScale.x, tree.lossyScale.z);
            Assert.Greater(closest, trunkRadius + 0.25f, "walked into the trunk");
        }

        [UnityTest]
        public IEnumerator TreesStopBullets()
        {
            var tree = TreeOnFlatGround();
            Assert.IsNotNull(tree);
            var p = new Vector2(tree.position.x, tree.position.z);
            yield return Teleport(p + Vector2.down * 3f, 0f);
            input.Aim = true;
            // aim at the trunk at chest height
            Vector3 aim = tree.position + Vector3.up * 1.3f;
            for (int i = 0; i < 4; i++)
            {
                rig.Snap();
                Vector3 d = (aim - rig.transform.position).normalized;
                rig.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                rig.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            }
            yield return Seconds(0.6f);
            ShotResult shot = default;
            bool fired = false;
            weapon.Fired += s =>
            {
                if (!fired) shot = s;
                fired = true;
            };
            input.Fire = true;
            for (int i = 0; i < 30 && !fired; i++) yield return null;
            input.Fire = false;
            Assert.IsTrue(fired);
            Assert.IsTrue(shot.Hit);
            Assert.AreEqual(tree, shot.Info.collider.transform, $"bullet hit {shot.Info.collider.name} instead of the tree");
        }

        [UnityTest]
        public IEnumerator TheBoundaryHolds()
        {
            yield return Teleport(new Vector2(ForestLayout.Boundary - 6f, 0f), 90f);
            input.Move = new Vector2(0f, 1f);
            input.Sprint = true;
            yield return Seconds(4f);
            input.Move = Vector2.zero;
            input.Sprint = false;
            Assert.Less(player.position.x, ForestLayout.Boundary, "walked off the edge of the world");
            Assert.Greater(player.position.y, -5f);
        }

        [UnityTest]
        public IEnumerator EveryTargetIsVisibleFromTheFiringLine()
        {
            var eye = new Vector3(14f, 1.6f, 0f);
            foreach (var t in DamageableTarget.All)
            {
                var head = t.GetComponentsInChildren<Hitbox>().First(h => h.name == "Head").transform.position;
                Assert.IsTrue(Physics.Linecast(eye, head, out var hit), $"{t.name}: nothing hit");
                Assert.AreEqual(t, hit.collider.GetComponentInParent<DamageableTarget>(), $"{t.name}: blocked by {hit.collider.name}");
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AmbienceLoops()
        {
            var amb = GameObject.Find("Forest/Ambience").GetComponent<AudioSource>();
            Assert.IsTrue(amb.loop);
            Assert.IsNotNull(amb.clip);
            Assert.IsTrue(amb.playOnAwake);
            yield return null;
        }

        // ------------------------------------------------------------ screenshots

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

            yield return Seconds(0.3f);
            Shot(dir, "m3_01_spawn_range");

            yield return Teleport(new Vector2(0f, -20f), 0f);
            rig.Pitch = 5f;
            yield return Seconds(0.3f);
            Shot(dir, "m3_02_yard_north");

            Vector2 a = ForestLayout.TrailPoint(0.3f), b = ForestLayout.TrailPoint(0.33f);
            yield return Teleport(a, Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(1f);
            input.Move = Vector2.zero;
            Shot(dir, "m3_03_trail");

            var pond = ForestLayout.PondCentre + new Vector2(0f, ForestLayout.PondRadius + 4f);
            yield return Teleport(pond, 180f);
            rig.Pitch = 12f;
            yield return Seconds(0.3f);
            Shot(dir, "m3_04_pond");

            // overview from above the yard, looking north-west to the lookout
            rig.enabled = false;
            rig.transform.SetPositionAndRotation(new Vector3(30f, 30f, -40f), Quaternion.Euler(18f, -40f, 0f));
            Shot(dir, "m3_05_overview");
            rig.transform.SetPositionAndRotation(new Vector3(0f, 22f, -10f), Quaternion.Euler(14f, 90f, 0f));
            Shot(dir, "m3_06_range_overview");
            rig.enabled = true;
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
