using System.Collections;
using System.IO;
using ForestSoldier.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForestSoldier.Tests
{
    /// <summary>
    /// Drives the soldier in ForestRange through SoldierInput.ExternalControl.
    /// Runs at a fixed 60 fps (Time.captureFramerate) so results are repeatable.
    /// Set FOREST_SOLDIER_SHOTS=1 to also write screenshots to Logs/Screens.
    /// </summary>
    public class SoldierMilestone1Tests
    {
        private SoldierInput input;
        private SoldierMotor motor;
        private SoldierAnimator anim;
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
            motor = input.GetComponent<SoldierMotor>();
            anim = input.GetComponent<SoldierAnimator>();
            rig = Object.FindFirstObjectByType<ThirdPersonCamera>();
            player = input.transform;
            input.ExternalControl = true;
            input.Move = Vector2.zero;
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
            rig.Pitch = 10f;
            rig.Snap();
            yield return Frames(5);
        }

        private float Param(string name) => anim.Animator.GetFloat(name);

        [UnityTest]
        public IEnumerator RunsForwardRelativeToCamera()
        {
            yield return Teleport(new Vector3(0f, 0.05f, 0f), 0f);
            rig.Yaw = 90f; // camera looks along +X
            Vector3 start = player.position;
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(1.5f);
            Vector3 d = player.position - start;
            Assert.Greater(d.x, 2.2f, "should travel ~2.6 m along the camera's forward");
            Assert.Less(Mathf.Abs(d.z), 0.3f);
            Assert.Greater(Vector3.Dot(player.forward, Vector3.right), 0.97f, "should turn to face movement");
            Assert.Greater(Param("MoveZ"), 1.4f);
            Assert.Less(Mathf.Abs(Param("MoveX")), 0.3f);
            Assert.That(motor.AnimationVelocity.magnitude, Is.EqualTo(motor.RunSpeed).Within(0.1f));
        }

        [UnityTest]
        public IEnumerator SprintIsFasterAndSettlesBackToIdle()
        {
            yield return Teleport(new Vector3(5f, 0.05f, -20f), 180f);
            input.Move = new Vector2(0f, 1f);
            input.Sprint = true;
            yield return Seconds(1.5f);
            Assert.That(motor.AnimationVelocity.magnitude, Is.EqualTo(motor.SprintSpeed).Within(0.2f));
            Assert.IsTrue(motor.IsSprinting);
            Assert.Greater(Param("MoveZ"), 5f);

            input.Move = Vector2.zero;
            input.Sprint = false;
            yield return Seconds(1f);
            Assert.Less(motor.AnimationVelocity.magnitude, 0.05f);
            Assert.Less(Mathf.Abs(Param("MoveZ")), 0.1f);
        }

        [UnityTest]
        public IEnumerator AimStrafeKeepsFacingTheCamera()
        {
            yield return Teleport(new Vector3(0f, 0.05f, 0f), 0f);
            input.Aim = true;
            input.Move = new Vector2(1f, 0f);
            Vector3 start = player.position;
            yield return Seconds(1f);
            Assert.Greater(Vector3.Dot(player.forward, Vector3.forward), 0.99f, "aiming keeps facing camera yaw");
            Assert.Greater((player.position - start).x, 1.3f);
            Assert.Greater(Param("MoveX"), 1.4f);

            rig.Yaw = 45f; // turning the camera while aiming turns the soldier
            input.Move = Vector2.zero;
            yield return Seconds(0.25f);
            Assert.That(player.eulerAngles.y, Is.EqualTo(45f).Within(1f));
        }

        [UnityTest]
        public IEnumerator WallStopsMovementAndAnimation()
        {
            yield return Teleport(new Vector3(20f, 0.05f, 0f), 0f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = new Vector3(20f, 1.5f, 4f);
            wall.transform.localScale = new Vector3(6f, 3f, 1f);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(4f);
            Assert.Less(player.position.z, 3.5f - 0.29f, "capsule must stop at the wall face");
            Assert.Less(motor.AnimationVelocity.magnitude, 0.2f, "animation should not run in place against a wall");
            Object.Destroy(wall);
        }

        [UnityTest]
        public IEnumerator CameraStaysInFrontOfAWallBehindThePlayer()
        {
            yield return Teleport(new Vector3(-20f, 0.05f, 0f), 0f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = new Vector3(-20f, 1.5f, -1.5f);
            wall.transform.localScale = new Vector3(8f, 4f, 0.5f);
            yield return Frames(3);
            Assert.Greater(rig.transform.position.z, -1.25f + 0.05f, "camera must not pass into the wall");
            Object.Destroy(wall);
            yield return Seconds(1f);
            Assert.Less(rig.transform.position.z, -2.5f, "camera eases back out once clear");
        }

        [UnityTest]
        public IEnumerator ClimbsGentleRampButNotSteepOne()
        {
            yield return Teleport(new Vector3(-8f, 0.05f, 6f), 0f);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(3.5f);
            Assert.Greater(player.position.y, 0.6f, "20 degree ramp is walkable");
            Assert.IsTrue(motor.IsGrounded);

            input.Move = Vector2.zero;
            yield return Teleport(new Vector3(4f, 0.05f, 6f), 0f);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(4f);
            Assert.Less(player.position.y, 0.8f, "60 degree ramp exceeds the slope limit");
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
            yield return Teleport(new Vector3(0f, 0.05f, 0f), 30f);
            yield return Seconds(0.5f);
            Shot(dir, "01_idle");

            input.Move = new Vector2(0f, 1f);
            yield return Seconds(1.2f);
            Shot(dir, "02_run");
            input.Sprint = true;
            yield return Seconds(1.0f);
            Shot(dir, "03_sprint");
            input.Sprint = false;

            input.Aim = true;
            input.Move = new Vector2(-1f, 0f);
            yield return Seconds(1.0f);
            Shot(dir, "04_aim_strafe_left");
            input.Move = new Vector2(1f, 1f).normalized;
            yield return Seconds(1.0f);
            Shot(dir, "05_aim_forward_right");
            input.Move = new Vector2(0f, -1f);
            yield return Seconds(1.0f);
            Shot(dir, "06_aim_back");
            input.Move = Vector2.zero;
            yield return Seconds(0.6f);
            Shot(dir, "07_aim_idle");

            // side view of the run cycle for foot sliding / facing checks
            input.Aim = false;
            yield return Teleport(new Vector3(-30f, 0.05f, 0f), 0f);
            input.Move = new Vector2(0f, 1f);
            yield return Seconds(1.0f);
            rig.enabled = false;
            rig.transform.SetPositionAndRotation(player.position + new Vector3(4f, 1.2f, 0f),
                Quaternion.LookRotation(Vector3.left));
            Shot(dir, "08_run_side");

            input.Move = Vector2.zero;
            input.Aim = true;
            rig.enabled = true;
            yield return Seconds(0.8f);
            rig.enabled = false;
            rig.transform.SetPositionAndRotation(player.position + player.right * 3f + Vector3.up * 1.4f,
                Quaternion.LookRotation(-player.right));
            Shot(dir, "09_aim_idle_side");
            rig.enabled = true;
            input.Aim = false;
        }

        private void Shot(string dir, string name)
        {
            var cam = rig.Camera;
            var rt = new RenderTexture(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render(); // first render after load can come out with placeholder materials
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
