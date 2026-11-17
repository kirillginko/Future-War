using System.Collections;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Destruction;
using ForestSoldier.Environment;
using ForestSoldier.NPC;
using ForestSoldier.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>
    /// Destructible houses (one skinned mesh each), the under-barrel grenade
    /// launcher and its blast, and mesh-particle shell casings.
    /// </summary>
    public class SoldierDestructionTests
    {
        private SoldierInput input;
        private HitscanWeapon rifle;
        private GrenadeLauncher launcher;
        private WeaponEffects effects;
        private ThirdPersonCamera rig;
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
            rifle = player.GetComponent<HitscanWeapon>();
            launcher = player.GetComponent<GrenadeLauncher>();
            effects = player.GetComponent<WeaponEffects>();
            rig = Object.FindFirstObjectByType<ThirdPersonCamera>();
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
            for (int i = 0; i < 4; i++)
            {
                rig.Snap();
                Vector3 d = (point - rig.transform.position).normalized;
                rig.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                rig.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            }
        }

        private IEnumerator SelectLauncherWeapon()
        {
            var loadout = player.GetComponent<WeaponLoadout>();
            if (loadout != null)
                for (int i = 0; i < loadout.Count && !launcher.Available; i++)
                {
                    loadout.Select(i);
                    yield return null;
                }
            Assume.That(launcher.Available, "no weapon in the loadout carries the launcher");
        }

        private static DestructibleStructure House(int i) =>
            DestructibleStructure.All.Where(h => h.name.StartsWith("House_")).OrderBy(h => h.name).ElementAt(i);

        private static DestructibleStructure Structure(string name) =>
            DestructibleStructure.All.First(h => h.name == name);

        /// <summary>A point just outside the middle of the house's front wall (local -Z).</summary>
        private static Vector3 FrontWallPoint(DestructibleStructure h, float height, float outside = 0.4f) =>
            h.transform.TransformPoint(new Vector3(-1.6f, height, -2.5f - outside));

        // ------------------------------------------------------------ houses

        [UnityTest]
        public IEnumerator EachHouseIsOneSkinnedMeshWithABonePerFragment()
        {
            Assert.AreEqual(ForestLayout.Houses.Length, DestructibleStructure.All.Count(h => h.name.StartsWith("House_")));
            foreach (var h in DestructibleStructure.All.Where(x => x.name.StartsWith("House_")))
            {
                var renderers = h.GetComponentsInChildren<Renderer>();
                Assert.AreEqual(1, renderers.Length, $"{h.name}: one renderer");
                var smr = renderers[0] as SkinnedMeshRenderer;
                Assert.IsNotNull(smr, $"{h.name}: it's a skinned mesh");
                Assert.AreEqual(1, smr.sharedMaterials.Length, $"{h.name}: one material, one draw");
                Assert.AreEqual(h.FragmentCount, smr.bones.Length);
                Assert.Greater(h.FragmentCount, 100);
                Assert.AreEqual(0, h.CountFloatingAttached());
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ABlastBreaksTheWallNearItAndNothingIsLeftFloating()
        {
            var h = House(0);
            var rest = Enumerable.Range(0, h.FragmentCount).Select(i => h.Fragment(i).position).ToArray();
            var report = launcher.Detonate(FrontWallPoint(h, 1.4f));
            Assert.Greater(report.FragmentsBroken, 6, "the blast takes a hole out of the wall");
            Assert.Less(h.DetachedCount, h.FragmentCount, "but not the whole house");
            Assert.AreEqual(0, h.CountFloatingAttached(), "anything cut off from the ground has fallen");
            yield return Seconds(2f);

            int moved = 0;
            for (int i = 0; i < h.FragmentCount; i++)
                if (h.IsDetached(i) && Vector3.Distance(rest[i], h.Fragment(i).position) > 0.3f) moved++;
            Assert.Greater(moved, 5, "broken pieces fly and fall");
            for (int i = 0; i < h.FragmentCount; i++)
                if (!h.IsDetached(i)) Assert.Less(Vector3.Distance(rest[i], h.Fragment(i).position), 1e-3f, "the rest stands");
            Assert.AreEqual(1, h.GetComponentsInChildren<Renderer>().Length, "still one skinned mesh in pieces");
        }

        [UnityTest]
        public IEnumerator KnockingOutTheWallsDropsTheRoof()
        {
            var h = House(1);
            // Blasts all round at waist height: walls go, the roof has nothing left to stand on.
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                launcher.Detonate(h.transform.TransformPoint(new Vector3(Mathf.Cos(a) * 1.4f, 1.2f, Mathf.Sin(a) * 1.1f)));
            }
            yield return Seconds(3f);
            Assert.AreEqual(0, h.CountFloatingAttached());
            float roofHeight = h.transform.position.y + 3f;
            int roofDown = 0;
            for (int i = 0; i < h.FragmentCount; i++)
            {
                var f = h.Fragment(i);
                if (f.GetComponent<BoxCollider>().size.y < 0.3f && f.GetComponent<BoxCollider>().size.x > 1.2f
                    && f.position.y < roofHeight - 0.8f) roofDown++;
            }
            Assert.Greater(roofDown, 3, "roof slabs come down");
        }

        [UnityTest]
        public IEnumerator DebrisSettlesIntoStaticRubbleAndResetRebuilds()
        {
            var h = House(2);
            var restLocal = Enumerable.Range(0, h.FragmentCount).Select(i => h.Fragment(i).localPosition).ToArray();
            launcher.Detonate(FrontWallPoint(h, 1.0f));
            yield return Frames(2);
            Assert.Greater(h.ActiveDebris, 0);
            yield return Seconds(22f);
            Assert.AreEqual(0, h.ActiveDebris, "debris has frozen");
            Assert.AreEqual(0, h.GetComponentsInChildren<Rigidbody>().Length, "no rigidbodies left");

            input.ResetTargetsRequested = true;
            yield return Frames(3);
            Assert.AreEqual(0, h.DetachedCount);
            for (int i = 0; i < h.FragmentCount; i++)
                Assert.Less(Vector3.Distance(restLocal[i], h.Fragment(i).localPosition), 1e-4f);
        }

        [UnityTest]
        public IEnumerator YardAndRangeStructuresAreDestructibleToo()
        {
            string[] names =
            {
                "Wall_North", "Wall_Corner_A", "Wall_Corner_B", "LowRoof", "Pillar_0", "Pillar_7",
                "FiringLine_Bench", "Cover_Crate_50m", "Cover_Wall_25m", "Backstop",
            };
            foreach (var n in names)
            {
                var st = Structure(n);
                Assert.AreEqual(1, st.GetComponentsInChildren<Renderer>().Length, $"{n}: one skinned mesh");
                Assert.GreaterOrEqual(st.FragmentCount, 6, $"{n}: fractured");
                Assert.AreEqual(0, st.CountFloatingAttached(), $"{n}: everything attached is supported");
            }

            var wall = Structure("Wall_North");
            var report = launcher.Detonate(wall.transform.position + new Vector3(0f, -0.3f, -0.6f));
            Assert.Greater(report.FragmentsBroken, 6, "a blast opens the yard wall");
            Assert.Less(wall.DetachedCount, wall.FragmentCount / 2, "but only near the blast: it's 30 m long");

            // Take out both posts of the low roof and the slab comes down.
            var roof = Structure("LowRoof");
            launcher.Detonate(new Vector3(6f, 1.0f, -5.2f));
            launcher.Detonate(new Vector3(10f, 1.0f, -5.2f));
            yield return Seconds(3f);
            int slabsDown = 0;
            for (int i = 0; i < roof.FragmentCount; i++)
            {
                var f = roof.Fragment(i);
                if (f.GetComponent<BoxCollider>().size.x > 1.2f && f.position.y < 1.5f) slabsDown++;
            }
            Assert.AreEqual(0, roof.CountFloatingAttached());
            Assert.Greater(slabsDown, 4, "roof panels on the ground");
        }

        [UnityTest]
        public IEnumerator BulletsTravelAndImpactsWaitForThem()
        {
            foreach (var w in WalkerNPC.All.ToArray()) w.gameObject.SetActive(false);
            yield return Teleport(new Vector3(14f, 0f, 0f), 90f);
            var target = DamageableTarget.All.First(t => t.name == "Target_60m");
            input.Aim = true;
            LookAt(target.GetComponentsInChildren<Hitbox>().First(h => h.name == "Torso").transform.position);
            yield return Seconds(0.8f);
            float health = target.Health;
            int impacts = effects.ImpactsShown;
            input.Fire = true;
            int fired = rifle.ShotsFired;
            for (int i = 0; i < 10 && rifle.ShotsFired == fired; i++) yield return null;
            input.Fire = false;
            Assert.Less(target.Health, health, "damage is instant (hitscan)");
            Assert.Greater(effects.TracersInFlight, 0, "the round is still on its way");
            if (System.Environment.GetEnvironmentVariable("FOREST_SOLDIER_SHOTS") == "1")
            {
                // Side view of the lane, a few frames into the flight.
                yield return Frames(4);
                rig.enabled = false;
                Vector3 eye = new Vector3(34f, 2.2f, -9f);
                rig.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(new Vector3(44f, 1.4f, 1f) - eye));
                string dir = Path.Combine(Application.dataPath, "../Logs/Screens");
                Directory.CreateDirectory(dir);
                Shot(dir, "b_01_bullet_in_flight");
                rig.enabled = true;
            }
            Assert.AreEqual(impacts, effects.ImpactsShown, "no sparks before it gets there");
            yield return Seconds(0.3f); // 60 m at 350 m/s is 0.17 s
            Assert.Greater(effects.ImpactsShown, impacts, "sparks when it arrives");
            Assert.AreEqual(0, effects.TracersInFlight);
        }

        // ------------------------------------------------------------ launcher

        [Test]
        public void LaunchSolutionLandsOnTheTarget()
        {
            var from = new Vector3(0f, 1.5f, 0f);
            foreach (var to in new[] { new Vector3(30f, 0f, 0f), new Vector3(0f, 0f, 70f), new Vector3(20f, 6f, 15f), new Vector3(-50f, -2f, 40f) })
            {
                var v = GrenadeLauncher.SolveLaunch(from, to, 55f, 9.81f);
                Vector3 p = from;
                float best = float.MaxValue;
                for (int i = 0; i < 2000; i++)
                {
                    float dt = 0.002f;
                    p += v * dt + 0.5f * dt * dt * Vector3.down * 9.81f;
                    v += Vector3.down * 9.81f * dt;
                    best = Mathf.Min(best, Vector3.Distance(p, to));
                }
                Assert.Less(best, 0.3f, $"arc misses {to} by {best:F2} m");
            }
        }

        [UnityTest]
        public IEnumerator LauncherRoundBlowsAHoleInAHouse()
        {
            yield return SelectLauncherWeapon();
            var h = House(3);
            // Solid wall under the left window (the middle is the doorway: you'd see, and shoot, through it).
            Vector3 wall = h.transform.TransformPoint(new Vector3(-1.6f, 0.6f, -2.5f));
            Vector3 stand = h.transform.TransformPoint(new Vector3(0f, 0f, -18f)); // on the trail, in the open
            foreach (var w in WalkerNPC.All.ToArray()) w.gameObject.SetActive(false);
            yield return Teleport(stand, h.transform.eulerAngles.y);
            input.SwitchLauncherRequested = true;
            yield return Frames(30);
            Assert.IsTrue(launcher.Active);
            LookAt(wall);
            yield return Frames(20);

            int rifleShots = rifle.ShotsFired;
            int reserve = launcher.Reserve;
            Vector3 boom = Vector3.zero;
            ExplosionReport report = default;
            bool exploded = false;
            launcher.Exploded += (at, r) => { exploded = true; boom = at; report = r; };
            input.Fire = true;
            yield return Frames(3);
            input.Fire = false;
            for (int i = 0; i < 240 && !exploded; i++) yield return null;
            Assert.IsTrue(exploded, "the round went off");
            Assert.Less(Vector3.Distance(boom, wall), 1.5f, $"landed {Vector3.Distance(boom, wall):F2} m from the aim point");
            Assert.Greater(report.FragmentsBroken, 5);
            Assert.AreEqual(rifleShots, rifle.ShotsFired, "the rifle stays quiet in launcher mode");
            yield return Seconds(2.2f);
            Assert.IsTrue(launcher.Loaded, "reloads itself");
            Assert.AreEqual(reserve - 1, launcher.Reserve);

            input.SwitchLauncherRequested = true;
            yield return Frames(3);
            Assert.IsFalse(launcher.Active, "G again goes back to the rifle");
        }

        [UnityTest]
        public IEnumerator BlastKillsAWalkerButAWallShieldsOne()
        {
            var w = WalkerNPC.All.First(x => x.name == "Walker_RangeSouth");
            var report = launcher.Detonate(w.transform.position + new Vector3(1.5f, 0.3f, 0f));
            Assert.IsFalse(w.IsAlive, "a walker 1.5 m from the blast dies");
            Assert.GreaterOrEqual(report.Killed, 1);

            // Behind a house: the walls take it.
            var h = House(0);
            var behind = WalkerNPC.All.First(x => x.name == "Walker_RangeNorth");
            behind.gameObject.SetActive(false);
            behind.transform.position = h.transform.TransformPoint(new Vector3(0f, 0f, 1.2f)); // inside the house
            behind.gameObject.SetActive(true);
            yield return null;
            launcher.Detonate(h.transform.TransformPoint(new Vector3(0f, 1.2f, -4.0f))); // 1.5 m out from the front wall, 5 m from them
            Assert.IsTrue(behind.IsAlive, "the front wall shields whoever is inside");
        }

        [UnityTest]
        public IEnumerator EveryWeaponFiresAndOnlyTheBizonCarriesTheLauncher()
        {
            var loadout = player.GetComponent<WeaponLoadout>();
            Assume.That(loadout, Is.Not.Null, "no weapon loadout on the player");
            yield return Teleport(new Vector3(14f, 0f, 0f), 90f);
            Assert.IsFalse(player.GetComponentsInChildren<Transform>(true).Any(t => t.name == "GrenadeLauncher"),
                "no add-on launcher model: the Bizon's own tube is the launcher");
            bool shots = System.Environment.GetEnvironmentVariable("FOREST_SOLDIER_SHOTS") == "1";
            string dir = Path.Combine(Application.dataPath, "../Logs/Screens");
            if (shots) Directory.CreateDirectory(dir);

            for (int i = 0; i < loadout.Count; i++)
            {
                loadout.Select(i);
                input.Aim = true;
                rig.Pitch = 0f;
                yield return Seconds(0.6f);
                string name = loadout.SelectedName;
                bool bizon = name.Contains("Bizon");
                Assert.AreEqual(bizon, launcher.Available, $"{name}: launcher availability");
                if (bizon)
                {
                    // Rounds leave from the front face of the Bizon's own under-barrel tube (the model's "Mag" part).
                    var tube = player.GetComponentsInChildren<Renderer>().First(r => r.name.StartsWith("Mag") && r.gameObject.activeInHierarchy);
                    Vector3 at = launcher.LaunchPosition;
                    float gap = Mathf.Sqrt(tube.bounds.SqrDistance(at));
                    Vector3 rel = loadout.Muzzle.InverseTransformPoint(at);
                    Debug.Log($"[weapons] {name}: launch point {at - loadout.Muzzle.position} from the muzzle, {gap * 100f:F1} cm from the tube's bounds");
                    Assert.Less(gap, 0.03f, "launch point on the tube");
                    Assert.Less(Vector3.Dot(at - loadout.Muzzle.position, loadout.Muzzle.up), -0.01f, "under the barrel");
                    Assert.Less(Vector3.Dot(at - loadout.Muzzle.position, loadout.Muzzle.forward), 0f, "behind the muzzle");
                }

                int casings = effects.CasingCount, fired = rifle.ShotsFired;
                rig.Pitch = -20f;
                input.Fire = true;
                yield return Seconds(0.4f);
                input.Fire = false;
                Assert.Greater(rifle.ShotsFired, fired, $"{name} fires");
                Assert.Greater(effects.CasingCount, casings, $"{name} ejects casings");

                if (shots)
                {
                    if (bizon)
                    {
                        input.SwitchLauncherRequested = true;
                        yield return Frames(20);
                    }
                    rig.Pitch = 0f;
                    yield return Seconds(0.4f);
                    rig.enabled = false;
                    Vector3 gun = loadout.Muzzle.position;
                    Vector3 eye = gun + player.right * 0.9f + Vector3.up * 0.15f - player.forward * 0.2f;
                    rig.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(gun - player.forward * 0.25f - eye));
                    Shot(dir, $"g_{i + 1}_{name.Replace(' ', '_')}");
                    rig.enabled = true;
                    if (bizon)
                    {
                        input.SwitchLauncherRequested = true;
                        yield return Frames(5);
                    }
                }
                input.Aim = false;
                yield return Frames(5);
            }
        }

        [UnityTest]
        public IEnumerator BlastDeathsRagdollAndResetStandsThemUp()
        {
            var w = WalkerNPC.All.First(x => x.name == "Walker_RangeSouth");
            yield return Frames(5);
            var chest = w.GetComponentsInChildren<Transform>().First(t => t.name == "Spineupper");
            var hand = w.GetComponentsInChildren<Transform>().First(t => t.name == "HandWrist.L");
            var handParent = hand.parent;
            Vector3 start = chest.position;
            float ground = Ground(w.transform.position);
            var report = launcher.Detonate(w.transform.position + w.transform.right * 1.6f + Vector3.up * 0.3f);
            Assert.IsFalse(w.IsAlive);
            Assert.IsTrue(w.Ragdolled, "a blast kill goes limp instead of animating");
            Assert.GreaterOrEqual(w.Ragdoll.Bodies.Count, 10, "pelvis, chest, head, arms and legs");
            Assert.IsFalse(w.GetComponentInChildren<Animator>().enabled);

            float peak = start.y, far = 0f;
            for (int i = 0; i < 60; i++)
            {
                yield return null;
                peak = Mathf.Max(peak, chest.position.y);
                far = Mathf.Max(far, Vector3.Distance(start, chest.position));
            }
            Assert.Greater(peak - start.y, 0.5f, "thrown up off the ground");
            Assert.Greater(far, 1.5f, "and away from the blast");
            Vector3 away = chest.position - (w.transform.position + w.transform.right * 1.6f);
            Assert.Greater(Vector3.Dot(away, -w.transform.right), 0f, "the right way: away from the blast");

            yield return Seconds(9f);
            Assert.Less(chest.position.y, ground + 0.7f, "lying on the ground by now");
            Assert.AreEqual(handParent != hand.parent, true, "hands ride the forearms while ragdolled");

            input.ResetTargetsRequested = true;
            yield return Frames(3);
            Assert.IsTrue(w.IsAlive);
            Assert.IsFalse(w.Ragdolled);
            Assert.AreEqual(0, w.GetComponentsInChildren<Rigidbody>().Length, "ragdoll bodies removed");
            Assert.AreEqual(0, w.GetComponentsInChildren<CharacterJoint>().Length);
            Assert.AreEqual(handParent, hand.parent, "bones back where the rig keeps them");
            Assert.IsTrue(w.GetComponentInChildren<Animator>().enabled);
            yield return Seconds(1f);
            Assert.Greater(w.CurrentSpeed, 0.3f, "walking again");
        }

        [UnityTest]
        public IEnumerator BulletKillsStillPlayTheDeathAnimation()
        {
            var w = WalkerNPC.All.First(x => x.name == "Walker_RangeNorth");
            for (int i = 0; i < 4; i++)
                w.ApplyDamage(new DamageInfo(25f, w.transform.position + Vector3.up, Vector3.up, -w.transform.forward, null));
            yield return Frames(10);
            Assert.IsFalse(w.IsAlive);
            Assert.IsFalse(w.Ragdolled);
            Assert.IsTrue(w.GetComponentInChildren<Animator>().enabled);
        }

        [UnityTest]
        public IEnumerator ExplosionsHaveTheFullSetOfEffectsAndScorchTheGround()
        {
            yield return Teleport(new Vector3(0f, 0f, -20f), 0f);
            var at = new Vector3(5f, Ground(new Vector3(5f, 0f, -14f)) + 0.15f, -14f);
            launcher.Detonate(at);
            yield return Frames(6);
            var fx = Object.FindObjectsByType<ExplosionEffect>(FindObjectsSortMode.None).OrderBy(e => Vector3.Distance(e.transform.position, at)).First();
            var names = fx.GetComponentsInChildren<ParticleSystem>().Where(p => p.particleCount > 0).Select(p => p.name).ToArray();
            foreach (var n in new[] { "Fireball", "Smoke", "Sparks", "Dirt", "Shockwave", "GroundDust", "Embers", "Rocks", "SmokeColumn" })
                Assert.Contains(n, names, $"{n} particles in the blast");
            Assert.AreEqual(1, fx.ScorchesShown, "a scorch mark on the ground");
            yield return Seconds(2.5f);
            var rocks = fx.GetComponentsInChildren<ParticleSystem>().First(p => p.name == "Rocks");
            var parts = new ParticleSystem.Particle[rocks.particleCount];
            rocks.GetParticles(parts);
            Assert.Greater(parts.Count(p => p.position.y > Ground(p.position) - 0.1f), parts.Length * 0.8f, "rocks land on the ground, not through it");

            input.ResetTargetsRequested = true;
            yield return Frames(2);
            Assert.IsFalse(Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Any(r => r.name.StartsWith("Scorch") && r.enabled),
                "reset clears the scorch marks");
        }

        [UnityTest]
        public IEnumerator RagdollScreenshots()
        {
            if (System.Environment.GetEnvironmentVariable("FOREST_SOLDIER_SHOTS") != "1")
            {
                Assert.Ignore("Set FOREST_SOLDIER_SHOTS=1 to capture screenshots.");
                yield break;
            }
            string dir = Path.Combine(Application.dataPath, "../Logs/Screens");
            Directory.CreateDirectory(dir);
            var w = WalkerNPC.All.First(x => x.name == "Walker_RangeSouth");
            yield return Frames(5);
            Vector3 p = w.transform.position;
            rig.enabled = false;
            Vector3 eye = p + new Vector3(-2f, 2.4f, 11f);
            rig.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(p + Vector3.up * 1.2f - eye));
            launcher.Detonate(p + w.transform.right * 1.6f + Vector3.up * 0.3f);
            yield return Frames(3);
            Shot(dir, "r_01_blast");
            yield return Frames(14);
            Shot(dir, "r_02_flying");
            yield return Frames(30);
            Shot(dir, "r_03_tumbling");
            yield return Seconds(6f);
            Shot(dir, "r_04_heap_and_scorch");
            rig.enabled = true;
        }

        // ------------------------------------------------------------ casings

        [UnityTest]
        public IEnumerator CasingsEjectBounceAndRestOnTheGround()
        {
            yield return Teleport(new Vector3(14f, 0f, 0f), 90f);
            rig.Pitch = -30f;
            int before = effects.CasingCount;
            input.Fire = true;
            yield return Seconds(1f);
            input.Fire = false;
            Assert.GreaterOrEqual(effects.CasingCount - before, 9, "one casing per shot");
            yield return Seconds(3f);
            var ps = GameObject.Find("Casings").GetComponent<ParticleSystem>();
            var parts = new ParticleSystem.Particle[ps.particleCount];
            ps.GetParticles(parts);
            int resting = parts.Count(p => p.velocity.magnitude < 0.5f && p.position.y > Ground(p.position) - 0.05f
                                           && p.position.y < Ground(p.position) + 0.3f);
            string detail = string.Join("; ", parts.Take(10).Select(p =>
                $"y{p.position.y - Ground(p.position):F2} v{p.velocity.magnitude:F2} at ({p.position.x:F1},{p.position.z:F1})"));
            Assert.Greater(resting, parts.Length * 0.8f, $"{resting}/{parts.Length} casings lie on the ground: {detail}");
            Assert.AreEqual(1, Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Count(x => x.name == "Casings"),
                "every casing is one particle system: one instanced draw");
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
            var h = House(0);
            yield return Teleport(h.transform.TransformPoint(new Vector3(-3f, 0f, -14f)), h.transform.eulerAngles.y);
            rig.enabled = false;
            Vector3 eye = h.transform.TransformPoint(new Vector3(-5f, 2.2f, -11f));
            rig.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(h.transform.TransformPoint(new Vector3(0f, 1.6f, 0f)) - eye));
            yield return Frames(2);
            Shot(dir, "d_01_house_intact");
            launcher.Detonate(FrontWallPoint(h, 1.3f));
            yield return Frames(8);
            Shot(dir, "d_02_blast");
            yield return Seconds(0.8f);
            Shot(dir, "d_03_debris_flying");
            yield return Seconds(4f);
            Shot(dir, "d_04_rubble");
            rig.enabled = true;

            yield return Teleport(new Vector3(14f, 0f, 2f), 90f);
            rig.Pitch = -10f;
            input.Fire = true;
            yield return Seconds(1.5f);
            input.Fire = false;
            yield return Seconds(1.5f);
            rig.enabled = false;
            var cps = GameObject.Find("Casings").GetComponent<ParticleSystem>();
            var cp = new ParticleSystem.Particle[cps.particleCount];
            cps.GetParticles(cp);
            Vector3 centre = cp.Length > 0 ? cp.Aggregate(Vector3.zero, (a, p) => a + p.position) / cp.Length : player.position;
            Vector3 low = centre + new Vector3(1.2f, 0.7f, -0.9f);
            rig.transform.SetPositionAndRotation(low, Quaternion.LookRotation(centre - low));
            Shot(dir, "d_05_casings");
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
