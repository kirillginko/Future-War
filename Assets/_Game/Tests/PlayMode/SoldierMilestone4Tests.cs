using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.NPC;
using ForestSoldier.Environment;
using ForestSoldier.Player;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>Polish checks: shot accuracy near and far, canopies fading for the camera, no per-frame garbage.</summary>
    public class SoldierMilestone4Tests
    {
        private SoldierInput input;
        private HitscanWeapon weapon;
        private ThirdPersonCamera rig;
        private FoliageFader fader;
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
            fader = rig.GetComponent<FoliageFader>();
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

        private static float Ground(float x, float z) =>
            Terrain.activeTerrain.SampleHeight(new Vector3(x, 0f, z)) + Terrain.activeTerrain.transform.position.y;

        private IEnumerator Teleport(Vector2 at, float yaw)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(new Vector3(at.x, Ground(at.x, at.y) + 0.05f, at.y), Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Pitch = 8f;
            rig.Snap();
            yield return Frames(5);
        }

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

        /// <summary>Fire one aimed shot; returns it and the angle between the crosshair and where it landed.</summary>
        private IEnumerator AimedShot(Vector3 point, System.Action<ShotResult, float, float> done)
        {
            input.Aim = true;
            LookAt(point);
            yield return Seconds(0.8f);
            bool fired = false;
            ShotResult shot = default;
            float angle = 0f, spread = 0f;
            void OnFired(ShotResult s)
            {
                if (fired) return;
                fired = true;
                shot = s;
                spread = weapon.CurrentSpread;
                angle = Vector3.Angle(rig.transform.forward, s.End - rig.transform.position);
            }
            weapon.Fired += OnFired;
            input.Fire = true;
            for (int i = 0; i < 10 && !fired; i++) yield return null;
            input.Fire = false;
            weapon.Fired -= OnFired;
            input.Aim = false;
            Assert.IsTrue(fired);
            done(shot, angle, spread);
            yield return Seconds(0.5f);
        }

        [UnityTest]
        public IEnumerator AimedShotsLandOnTheCrosshairNearAndFar()
        {
            Random.InitState(4);
            yield return Teleport(new Vector2(14f, 0f), 90f);
            foreach (var name in new[] { "Target_10m", "Target_60m" } /* static targets: a mover can walk out of the line */)
            {
                var t = DamageableTarget.All.First(x => x.name == name);
                Vector3 torso = t.GetComponentsInChildren<Hitbox>().First(h => h.name == "Torso").transform.position;
                ShotResult shot = default;
                float angle = 0f, spread = 0f;
                yield return AimedShot(torso, (s, a, sp) => { shot = s; angle = a; spread = sp; });
                Assert.LessOrEqual(angle, spread + 0.1f, $"{name}: landed {angle:F2} deg off the crosshair (spread {spread:F2})");
                Assert.IsTrue(shot.Hit, $"{name}: missed everything");
                Assert.AreEqual(t, shot.Info.collider.GetComponentInParent<DamageableTarget>(), $"{name}: hit {shot.Info.collider.name}");
            }

            // point blank: the bench 3 m away
            var bench = GameObject.Find("FiringLine_Bench").transform;
            yield return Teleport(new Vector2(14f, -2.2f), 90f);
            ShotResult near = default;
            float nearAngle = 0f, nearSpread = 0f;
            yield return AimedShot(bench.position + Vector3.up * 0.2f, (s, a, sp) => { near = s; nearAngle = a; nearSpread = sp; });
            Assert.AreEqual(bench, near.Info.collider.transform, $"point blank hit {near.Info.collider?.name}");
            Assert.LessOrEqual(nearAngle, nearSpread + 1.5f, "point blank: the muzzle sits below the camera, so allow a little parallax");
        }

        private Transform TreeWithClearSouthSide()
        {
            foreach (Transform t in GameObject.Find("Forest/Trees").transform)
            {
                var p = new Vector2(t.position.x, t.position.z);
                if (ForestLayout.DistanceToClearing(p) > 25f) continue;
                if (!t.name.StartsWith("Pine")) continue;
                var stand = p + Vector2.up * 2.2f;
                var mid = new Vector3(stand.x, Ground(stand.x, stand.y) + 1f, stand.y);
                if (Physics.OverlapSphere(mid, 0.6f, ~(1 << 9)).Any(c => !(c is TerrainCollider))) continue;
                return t;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator CanopiesFadeForTheCameraAndComeBack()
        {
            Assert.IsNotNull(fader, "camera has a FoliageFader");
            var tree = TreeWithClearSouthSide();
            Assert.IsNotNull(tree);
            var r = tree.GetComponent<MeshRenderer>();
            var p = new Vector2(tree.position.x, tree.position.z);

            // Stand just north of the tree, facing north: the camera sits back in its canopy.
            yield return Teleport(p + Vector2.up * 2.2f, 0f);
            rig.Pitch = 20f;
            yield return Frames(10);
            Assert.IsTrue(r.sharedMaterials.Any(m => m.name.EndsWith("_Faded")), "the canopy around the camera is faded");
            Assert.Greater(fader.FadedCount, 0);

            yield return Teleport(new Vector2(0f, -10f), 0f);
            yield return Frames(15);
            Assert.IsFalse(r.sharedMaterials.Any(m => m.name.EndsWith("_Faded")), "and opaque again once the camera leaves");
        }

        /// <summary>
        /// The editor itself allocates a few hundred bytes a frame in play mode,
        /// so this compares against an idle baseline and guards against real
        /// per-frame garbage (strings, LINQ, boxing). The built player's numbers
        /// come from the benchmark run.
        /// </summary>
        [UnityTest]
        public IEnumerator NoExtraGarbageWhileMovingAimingAndFiring()
        {
            yield return Teleport(ForestLayout.TrailPoint(0.3f), 270f);
            using var alloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            yield return Seconds(1f);
            var idle = new List<long>(90);
            for (int i = 0; i < 90; i++)
            {
                yield return null;
                idle.Add(alloc.LastValue);
            }

            input.Move = new Vector2(0.3f, 1f);
            input.Aim = true;
            rig.Pitch = -25f; // fire at the sky: hits allocate a damage-number string, by design
            yield return Seconds(1f); // warm-up: first-time pools and fader lookups
            input.Fire = true;
            var busy = new List<long>(120);
            for (int i = 0; i < 120; i++)
            {
                rig.Yaw += 0.2f;
                yield return null;
                busy.Add(alloc.LastValue);
            }
            input.Fire = false;
            input.Move = Vector2.zero;
            input.Aim = false;
            double extra = busy.Average() - idle.Average();
            Debug.Log($"[M4] GC per frame: idle avg {idle.Average():F0} B, busy avg {busy.Average():F0} B (max {busy.Max()} B), extra {extra:F0} B");
            Assert.Less(extra, 128.0, $"moving, aiming and firing adds {extra:F0} B of garbage a frame");
        }
    }
}
