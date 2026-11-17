using System.Collections;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>
    /// Xbox-style controller support, driven through a virtual Gamepad device
    /// and the real Input System path (no ExternalControl).
    /// </summary>
    public class SoldierGamepadTests
    {
        private SoldierInput input;
        private SoldierMotor motor;
        private HitscanWeapon weapon;
        private ThirdPersonCamera rig;
        private Transform player;
        private Gamepad pad;
        private GamepadState state;

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
            pad = InputSystem.AddDevice<Gamepad>("VirtualXboxPad");
            state = new GamepadState();
            input.SetCursorLocked(true);
            yield return Frames(5);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (pad != null) InputSystem.RemoveDevice(pad);
            Time.captureFramerate = 0;
            yield return null;
        }

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        private static IEnumerator Seconds(float s) => Frames(Mathf.RoundToInt(s * 60f));

        private void Push() => InputSystem.QueueStateEvent(pad, state);

        private IEnumerator Hold(GamepadButton b, bool down)
        {
            state = state.WithButton(b, down);
            Push();
            yield return Frames(2);
        }

        private IEnumerator Press(GamepadButton b)
        {
            yield return Hold(b, true);
            yield return Hold(b, false);
        }

        private IEnumerator Teleport(Vector3 pos, float yaw)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Pitch = 5f;
            rig.Snap();
            yield return Frames(5);
        }

        [UnityTest]
        public IEnumerator LeftStickMovesRelativeToTheCamera()
        {
            yield return Teleport(new Vector3(0f, 0.05f, 0f), 0f);
            rig.Yaw = 90f;
            Vector3 start = player.position;
            state.leftStick = new Vector2(0f, 1f);
            Push();
            yield return Seconds(1.5f);
            state.leftStick = Vector2.zero;
            Push();
            Vector3 d = player.position - start;
            Assert.Greater(d.x, 1f, "full forward on the stick walks along the camera's forward");
            Assert.Less(Mathf.Abs(d.z), 0.3f);
            Assert.IsTrue(input.UsingGamepad);
        }

        [UnityTest]
        public IEnumerator RightStickTurnsWithAResponseCurve()
        {
            yield return Teleport(new Vector3(0f, 0.05f, 0f), 0f);
            float yaw0 = rig.Yaw;
            state.rightStick = new Vector2(1f, 0f);
            Push();
            yield return Seconds(1f);
            float full = Mathf.Repeat(rig.Yaw - yaw0, 360f);
            Assert.That(full, Is.InRange(170f, 330f), "a second at full right turns 200+ degrees (with the hold boost)");

            state.rightStick = new Vector2(0.3f, 0f);
            Push();
            yield return null;
            float yaw1 = rig.Yaw;
            yield return Seconds(1f);
            float slow = Mathf.Repeat(rig.Yaw - yaw1, 360f);
            Assert.That(slow, Is.InRange(5f, 45f), "a light push turns slowly for fine aim");

            state.rightStick = new Vector2(0f, 1f);
            Push();
            float pitch0 = rig.Pitch;
            yield return Seconds(0.4f);
            Assert.Less(rig.Pitch, pitch0 - 30f, "stick up looks up");
            state.rightStick = Vector2.zero;
            Push();
        }

        [UnityTest]
        public IEnumerator TriggersAimAndFire()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = -30f; // at the sky
            state.leftTrigger = 1f;
            Push();
            yield return Frames(3);
            Assert.IsTrue(input.Aim);
            int shots = weapon.ShotsFired;
            state.rightTrigger = 1f;
            Push();
            yield return Seconds(0.5f);
            state.rightTrigger = 0f;
            state.leftTrigger = 0f;
            Push();
            Assert.Greater(weapon.ShotsFired - shots, 3);
        }

        [UnityTest]
        public IEnumerator LeftBumperShowsTheFrontWithoutInterruptingFire()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = -15f;
            state.rightTrigger = 1f;
            Push();
            yield return Frames(15);
            int shots = weapon.ShotsFired;

            yield return Press(GamepadButton.LeftShoulder);
            yield return Frames(15);
            Assert.IsTrue(rig.IsFrontView);
            Assert.Greater(Vector3.Dot(rig.transform.position - player.position, player.forward), 0.4f);
            Assert.Less(Vector3.Dot(rig.transform.forward, rig.AimRay.direction), -0.8f);
            Assert.Greater(weapon.ShotsFired, shots, "fire continues when the presentation camera flips");
            Assert.Greater(Vector3.Dot(weapon.Muzzle.forward, rig.AimRay.direction), 0.95f);

            yield return Press(GamepadButton.LeftShoulder);
            Assert.IsFalse(rig.IsFrontView);
            Assert.Greater(Vector3.Dot(rig.transform.forward, rig.AimRay.direction), 0.99f);
            state.rightTrigger = 0f;
            Push();
        }

        [UnityTest]
        public IEnumerator RightStickClickEntersFirstPersonAndLeftTriggerFramesTheSights()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = 14f;
            var aimRig = player.GetComponent<SoldierAimRig>();
            var model = player.GetComponent<SoldierAnimator>().Animator;
            var meshes = model.GetComponentsInChildren<SkinnedMeshRenderer>();
            var originalShadows = System.Array.ConvertAll(meshes, r => r.shadowCastingMode);

            yield return Press(GamepadButton.RightStick);
            Assert.IsTrue(rig.IsFirstPerson);
            Assert.That(Vector3.Distance(rig.transform.position, aimRig.EyePosition), Is.LessThan(0.15f));
            state.leftStick = Vector2.up;
            Push();
            yield return Frames(18);
            Assert.IsTrue(motor.IsSprinting);
            for (int i = 0; i < meshes.Length; i++)
            {
                string mesh = meshes[i].name;
                bool visible = mesh == "Object_7" || mesh == "Object_13" || mesh == "Object_23";
                var expected = visible ? originalShadows[i] : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                Assert.AreEqual(expected, meshes[i].shadowCastingMode, mesh);
            }
            state.leftStick = Vector2.zero;

            state.leftTrigger = 1f;
            Push();
            yield return Frames(45);
            Assert.IsTrue(rig.ShowsIronSights);
            var sight = rig.Camera.WorldToViewportPoint(aimRig.SightPosition);
            Assert.Greater(sight.z, 0.2f);
            Assert.That(sight.x, Is.EqualTo(0.5f).Within(0.02f));
            Assert.That(sight.y, Is.EqualTo(0.5f).Within(0.02f));

            int shots = weapon.ShotsFired;
            state.rightTrigger = 1f;
            Push();
            yield return Frames(10);
            Assert.Greater(weapon.ShotsFired, shots);
            state.rightTrigger = 0f;
            state.leftTrigger = 0f;
            Push();
            yield return Press(GamepadButton.RightStick);
            Assert.IsFalse(rig.IsFirstPerson);
            CollectionAssert.AreEqual(originalShadows, System.Array.ConvertAll(meshes, r => r.shadowCastingMode));
        }

        [UnityTest]
        public IEnumerator StickDeflectionSetsThePace()
        {
            yield return Teleport(new Vector3(5f, 0.05f, -20f), 180f);
            float Speed() => motor.AnimationVelocity.magnitude;

            state.leftStick = new Vector2(0f, 0.35f);
            Push();
            yield return Seconds(1f);
            Assert.IsTrue(input.MoveIsAnalog);
            Assert.That(Speed(), Is.LessThan(SoldierMotor.WalkSpeed).And.GreaterThan(0.3f), "a light push creeps");

            state.leftStick = new Vector2(0f, 0.5f);
            Push();
            yield return Seconds(0.8f);
            Assert.That(Speed(), Is.EqualTo(SoldierMotor.WalkSpeed).Within(0.1f), "half tilt walks");

            yield return Teleport(new Vector3(5f, 0.05f, -2f), 180f);
            state.leftStick = new Vector2(0f, 1f);
            Push();
            yield return Seconds(1.2f);
            Assert.Greater(Speed(), motor.SprintSpeed * 0.9f, "full tilt runs flat out");
            Assert.IsTrue(motor.IsSprinting);

            state.leftStick = Vector2.zero;
            Push();
            yield return Seconds(0.6f);
            Assert.Less(Speed(), 0.05f);
        }

        [UnityTest]
        public IEnumerator BTogglesCrouch()
        {
            yield return Press(GamepadButton.East);
            yield return Frames(3);
            Assert.IsTrue(motor.IsCrouching, "B crouches");
            yield return Press(GamepadButton.East);
            yield return Frames(3);
            Assert.IsFalse(motor.IsCrouching, "B again stands");
        }

        [UnityTest]
        public IEnumerator MenuTogglesPauseAndViewResetsTargets()
        {
            state.leftStick = Vector2.up;
            Push();
            yield return Frames(5);
            yield return Press(GamepadButton.Start);
            Assert.IsFalse(input.CursorLocked, "Menu pauses");
            Assert.AreEqual(Vector2.zero, input.Move, "the stick cannot move the soldier while paused");
            state.leftStick = Vector2.zero;
            Push();
            yield return Press(GamepadButton.Start);
            Assert.IsTrue(input.CursorLocked, "Menu again resumes");

            var t = DamageableTarget.All[0];
            t.ApplyDamage(new DamageInfo(1000f, t.transform.position, Vector3.up, Vector3.right, null));
            yield return Press(GamepadButton.Select);
            Assert.IsTrue(t.IsAlive, "View resets the targets");
        }

        [UnityTest]
        public IEnumerator XReloads()
        {
            yield return Teleport(new Vector3(14f, 0.05f, 0f), 90f);
            rig.Pitch = -30f;
            state.rightTrigger = 1f;
            Push();
            yield return Seconds(0.4f);
            state.rightTrigger = 0f;
            Push();
            yield return Frames(2);
            Assert.Less(weapon.Ammo, weapon.MagazineSize);
            yield return Press(GamepadButton.West);
            Assert.IsTrue(weapon.IsReloading);
        }

        [UnityTest]
        public IEnumerator PromptsFollowTheLastDevice()
        {
            state.leftStick = new Vector2(0.5f, 0f);
            Push();
            yield return Frames(2);
            state.leftStick = Vector2.zero;
            Push();
            yield return Frames(2);
            Assert.IsTrue(input.UsingGamepad);

            // Headless runs have no focused Game view, so keyboard events would be dropped.
            var original = InputSystem.settings;
            var settings = Object.Instantiate(original);
            settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = settings;
            var keyboard = InputSystem.AddDevice<Keyboard>("TestKeyboard");
            try
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                yield return Frames(2);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return Frames(2);
                Assert.IsFalse(input.UsingGamepad, "keyboard input switches the prompts back");

                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.V));
                yield return Frames(2);
                Assert.IsTrue(rig.IsFrontView, "V faces the soldier");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.C));
                yield return Frames(2);
                Assert.IsTrue(rig.IsFirstPerson, "C switches to first person");
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                InputSystem.settings = original;
                Object.Destroy(settings);
            }
        }
    }
}
