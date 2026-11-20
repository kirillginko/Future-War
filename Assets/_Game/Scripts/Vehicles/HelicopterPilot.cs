using System;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Vehicles
{
    /// <summary>
    /// Lets the soldier fly the Little Bird on top of the Base Helicopter
    /// Controller asset (<see cref="HelicopterController"/>), unchanged:
    ///
    /// - Input: the asset reads keys with the legacy Input class, which this
    ///   project has switched off, so its ControlPanel stays disabled and this
    ///   component raises the same <c>ControlPanel.KeyPressed</c> event from
    ///   the Input System (keyboard, mouse or gamepad).
    /// - Altitude: the asset flies on a manual throttle (EngineForce). Here the
    ///   throttle is set every physics step to hold the climb rate you ask for
    ///   (Space/A up, Ctrl/B down, nothing = hover), allowing for its lift
    ///   fading with height and for the tilt.
    /// - Weight: the asset works out how far to lean (its tilt) and pushes along
    ///   the nose, but here the airframe has rotational inertia. Pitch and roll
    ///   swing towards that lean like a mass on a spring and overshoot a touch;
    ///   the turn rate builds up and runs down. Light drag lets it carry speed,
    ///   so you lean into a move, it gathers way over seconds, coasts when you
    ///   let go and brakes when you lean back. It leans into turns at speed.
    /// - Air: never still. Turbulence pushes on pitch, roll and yaw (more at
    ///   speed and in its own rotor wash near the ground), and slow gusts and
    ///   bumps push the whole helicopter about a little.
    /// - Rotors: their own run-up (slow, then fast) and run-down, apart from
    ///   the throttle, so they never slow while you descend. There is no lift
    ///   until they are nearly up to speed.
    ///
    /// Getting in (F / D-pad up, within <see cref="boardingRange"/>) hides the
    /// soldier in the seat and hands the camera to <see cref="HelicopterCamera"/>.
    /// Getting out puts him down beside the door.
    /// </summary>
    [DefaultExecutionOrder(-40)] // after SoldierInput, before the weapons
    [RequireComponent(typeof(HelicopterController), typeof(Rigidbody))]
    public class HelicopterPilot : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private HelicopterController controller;
        [SerializeField] private ControlPanel controlPanel;
        [SerializeField] private Transform seat;
        [SerializeField] private Transform exitPoint;
        [SerializeField] private HelicopterCamera chaseCamera;

        [Header("Boarding")]
        [SerializeField] private float boardingRange = 5f;

        [Header("Altitude hold")]
        [SerializeField] private float climbRate = 8f;
        [SerializeField] private float descendRate = 12f;
        [Tooltip("Throttle per m/s of climb-rate error.")]
        [SerializeField] private float climbGain = 1.2f;
        [SerializeField] private float maxEngineForce = 40f;
        [Tooltip("Engine run-up and run-down per second when getting in and out.")]
        [SerializeField] private float spoolRate = 12f;
        [Tooltip("Let go of climb/descend and it settles back to this many metres per second per metre off the height you let go at.")]
        [SerializeField] private float holdGain = 0.7f;
        [Tooltip("Extra vertical push (per m/s of climb-rate error) so it stops climbing or sinking without bouncing.")]
        [SerializeField] private float verticalAssist = 1.0f;
        [Tooltip("Weight on the collective: how fast the climb or sink rate builds, m/s².")]
        [SerializeField] private float verticalAccel = 4f;
        [Tooltip("How fast a climb or sink is checked (let go, or the landing cushion), m/s².")]
        [SerializeField] private float verticalBrake = 6f;
        [Tooltip("Near the ground the descent eases off to this many m/s per metre of height (it won't slam in).")]
        [SerializeField] private float landingCushion = 0.8f;
        [Tooltip("The slowest the cushion lets it come down, m/s.")]
        [SerializeField] private float touchdownRate = 2.5f;
        [Tooltip("Hands off below this height (m) and it settles onto the skids rather than holding a hover.")]
        [SerializeField] private float settleHeight = 1.2f;

        [Header("Handling")]
        [Tooltip("The asset's input ramp runs this many times per physics step: higher is snappier.")]
        [Range(1, 6)] [SerializeField] private int responseBoost = 2;
        [Tooltip("Stick past this counts as pressed (the asset's controls are on/off).")]
        [SerializeField] private float stickThreshold = 0.3f;
        [Tooltip("Pedal per mouse pixel moved in a physics step: small moves, small turns.")]
        [SerializeField] private float mouseYawGain = 0.2f;
        [Tooltip("Pedals (stick, bumpers, mouse) are analog: below this they count as centred.")]
        [SerializeField] private float pedalDeadzone = 0.1f;
        [Tooltip("Pedal turn rate at full input, degrees per second (the asset's own torque is a gentle drift).")]
        [SerializeField] private float yawRate = 75f;
        [Tooltip("How fast the turn rate builds, degrees per second squared.")]
        [SerializeField] private float yawAccel = 150f;
        [Tooltip("How fast it runs down when you let go of the pedals, degrees per second squared.")]
        [SerializeField] private float yawStop = 80f;
        [Tooltip("Turn rate from banking at speed, degrees per second at full bank.")]
        [SerializeField] private float bankTurnRate = 35f;
        [Tooltip("Forward speed (m/s) at which banking turns at the full rate.")]
        [SerializeField] private float bankTurnSpeed = 20f;

        [Header("Momentum")]
        [Tooltip("Share of the leaning lift's sideways push that moves it. Lower is heavier: it still leans a lot, but gathers and sheds speed slowly and carries it.")]
        [Range(0.1f, 1f)] [SerializeField] private float thrustShare = 0.56f; // full 40° lean pushes like a full 32° lean did at 0.75

        [Header("Weight (attitude)")]
        [Tooltip("How many times a second pitch and roll would swing if nothing damped them: lower is heavier.")]
        [SerializeField] private float swingFrequency = 0.8f;
        [Tooltip("Below 1 it overshoots the lean a little; lower overshoots more.")]
        [SerializeField] private float swingDamping = 0.55f;
        [Tooltip("Pitch (nose up and down) swings slower than roll: the long fuselage is heavier to tip. Swings a second.")]
        [SerializeField] private float pitchFrequency = 0.45f;
        [Tooltip("Pitch damping: below 1 it overshoots a little.")]
        [SerializeField] private float pitchDamping = 0.65f;
        [Tooltip("Bank into a turn at speed: degrees of roll per degree/s of turn rate.")]
        [SerializeField] private float turnLean = 0.12f;
        [Tooltip("Flapback: airspeed blows the rotor back and lifts the nose, degrees per m/s. Let go at speed and the nose comes up, then eases down as it slows.")]
        [SerializeField] private float flapback = 0.06f;
        [Tooltip("Roll from the tail rotor's reaction: degrees/s² per degree/s² of yaw acceleration.")]
        [SerializeField] private float torqueReaction = 0.06f;

        [Header("Air")]
        [Tooltip("Turbulence on pitch and roll in a still hover, degrees/s².")]
        [SerializeField] private float turbulenceHover = 150f;
        [Tooltip("Extra turbulence at top speed, degrees/s².")]
        [SerializeField] private float turbulenceSpeed = 320f;
        [Tooltip("Extra from its own rotor wash low over the ground, degrees/s².")]
        [SerializeField] private float rotorWash = 80f;
        [Tooltip("Turbulence on yaw, degrees/s².")]
        [SerializeField] private float yawTurbulence = 60f;
        [Tooltip("Slow gusts that push it about, m/s².")]
        [SerializeField] private float gust = 0.6f;
        [Tooltip("Up-and-down bumps, m/s² (more at speed).")]
        [SerializeField] private float bump = 1.8f;
        [Tooltip("Speed (m/s) treated as top speed for the turbulence.")]
        [SerializeField] private float turbulenceTopSpeed = 30f;

        [Header("Rotors")]
        [Tooltip("Seconds from still to full speed on getting in.")]
        [SerializeField] private float spinUpTime = 4f;
        [Tooltip("Seconds from full speed to still once you get out.")]
        [SerializeField] private float spinDownTime = 9f;
        [Tooltip("Lift is available from this rotor speed (0..1) and full at 1.")]
        [SerializeField] private float liftFromRotor = 0.6f;
        [SerializeField] private float mainRotorDegPerSec = 1440f;
        [SerializeField] private float tailRotorDegPerSec = 2600f;

        private Rigidbody body;
        private SoldierInput input;
        private Transform player;
        private readonly PressedKeyCode[][] buffers = new PressedKeyCode[9][];
        private Behaviour[] onFoot;
        private bool[] onFootWasEnabled;
        private CharacterController playerController;
        private Renderer[] playerRenderers;
        private bool[] playerRendererWasOn;
        private float mouseYawAccum;
        private bool holding;
        private float holdY;
        private float commandVy;
        private float lastContact = -1f; // the climb rate being flown: it eases towards the one asked for
        private float rotor;
        private AudioSource rotorAudio;
        private float rotorVolume = 0.8f;
        private float pitch, roll, pitchVel, rollVel, yawVel, seed; // degrees, degrees/s
        private Vector2 tiltTarget;

        public bool Occupied { get; private set; }
        public bool PlayerInRange => !Occupied && player != null && Vector3.Distance(player.position, transform.position) <= boardingRange;
        public bool OnGround => controller != null && controller.IsOnGround;
        public float Altitude { get; private set; }
        public float Speed => body != null ? body.linearVelocity.magnitude : 0f;
        public float EngineForce => controller != null ? controller.EngineForce : 0f;
        /// <summary>Rotor speed, 0 (still) to 1 (flying speed).</summary>
        public float RotorSpeed => rotor;
        /// <summary>Signed turn rate about world up, degrees per second.</summary>
        public float YawRate => body != null ? body.angularVelocity.y * Mathf.Rad2Deg : 0f;
        /// <summary>How far pitch (x) and roll (y) are off the lean the stick asks for, degrees: weight and air.</summary>
        public Vector2 Wobble => new Vector2(pitch, roll) - tiltTarget;
        public SoldierInput Input => input;
        public Rigidbody Body => body;

        public event Action Entered;
        public event Action Exited;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            lastContact = Time.fixedTime; // parked on its skids
            if (controller == null) controller = GetComponent<HelicopterController>();
            if (controlPanel == null) controlPanel = GetComponentInChildren<ControlPanel>(true);
            // The asset's panel polls the legacy Input class every physics step; we raise its event instead.
            if (controlPanel != null) controlPanel.enabled = false;
            // The asset works out the lean; we turn the airframe towards it with inertia (see Attitude).
            if (controller != null) controller.ExternalAttitude = true;
            for (int i = 0; i < buffers.Length; i++) buffers[i] = new PressedKeyCode[i];
            seed = UnityEngine.Random.Range(0f, 100f);
            rotorAudio = controller != null ? controller.HelicopterSound : null;
            if (rotorAudio != null) rotorVolume = rotorAudio.volume;
        }

        private void Start()
        {
            input = FindFirstObjectByType<SoldierInput>();
            if (input == null) return;
            player = input.transform;
            playerController = player.GetComponent<CharacterController>();
            // What the soldier stops doing while he's flying.
            onFoot = new Behaviour[]
            {
                player.GetComponent<SoldierMotor>(), player.GetComponent<HitscanWeapon>(), player.GetComponent<GrenadeLauncher>(),
                player.GetComponent<WeaponLoadout>(),
            };
            onFootWasEnabled = new bool[onFoot.Length];
            playerRenderers = player.GetComponentsInChildren<Renderer>(true);
            playerRendererWasOn = new bool[playerRenderers.Length];
            controller.EngineForce = 0f;
        }

        private void Update()
        {
            if (input == null || !input.InteractRequested) return;
            if (Occupied)
            {
                input.InteractRequested = false;
                Exit();
            }
            else if (PlayerInRange)
            {
                input.InteractRequested = false;
                Enter();
            }
        }

        // ------------------------------------------------------------ boarding

        public void Enter()
        {
            if (Occupied || input == null) return;
            Occupied = true;
            for (int i = 0; i < onFoot.Length; i++)
            {
                if (onFoot[i] == null) continue;
                onFootWasEnabled[i] = onFoot[i].enabled;
                onFoot[i].enabled = false;
            }
            if (playerController != null) playerController.enabled = false;
            for (int i = 0; i < playerRenderers.Length; i++)
            {
                playerRendererWasOn[i] = playerRenderers[i].enabled;
                playerRenderers[i].enabled = false;
            }
            player.SetParent(seat != null ? seat : transform, false);
            player.localPosition = Vector3.zero;
            player.localRotation = Quaternion.identity;
            input.ClearOnFootRequests(); // Y both boards and cycles weapons: the boarding press wins
            if (chaseCamera != null) chaseCamera.Engage(this);
            Entered?.Invoke();
        }

        public void Exit()
        {
            if (!Occupied) return;
            Occupied = false;
            player.SetParent(null, true);
            Vector3 at = exitPoint != null ? exitPoint.position : transform.position + transform.right * 2.5f;
            // Put him on the ground below the door if there is some within reach.
            if (Physics.Raycast(at + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                at = hit.point + Vector3.up * 0.05f;
            player.SetPositionAndRotation(at, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            if (playerController != null) playerController.enabled = true;
            for (int i = 0; i < onFoot.Length; i++)
                if (onFoot[i] != null) onFoot[i].enabled = onFootWasEnabled[i];
            for (int i = 0; i < playerRenderers.Length; i++)
                if (playerRenderers[i] != null) playerRenderers[i].enabled = playerRendererWasOn[i];
            input.ClearOnFootRequests();
            if (chaseCamera != null) chaseCamera.Release();
            Exited?.Invoke();
        }

        // ------------------------------------------------------------ flying

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Altitude = HeightAboveGround();
            // The asset sets IsOnGround on any collision enter and clears it on any exit, so with several
            // colliders (two skids, cabin, pods) one skid lifting clears it while the other still rests.
            // On the ground = something touched it in the last physics step.
            controller.IsOnGround = Time.fixedTime - lastContact <= dt * 1.5f;
            if (controlPanel == null || controlPanel.KeyPressed == null) return;

            // Rotors: run up slowly then quickly (the speed squared), run down slowly.
            rotor = Occupied ? Mathf.MoveTowards(rotor, 1f, dt / spinUpTime) : Mathf.MoveTowards(rotor, 0f, dt / spinDownTime);

            int n = 0;
            var keys = buffers[8];
            float pedal = 0f, bank = 0f;
            float ascend = 0f;
            if (Occupied && input != null)
            {
                Vector2 m = input.Move;
                if (m.y > stickThreshold) keys[n++] = PressedKeyCode.ForwardPressed;
                else if (m.y < -stickThreshold) keys[n++] = PressedKeyCode.BackPressed;
                if (m.x > stickThreshold) keys[n++] = PressedKeyCode.RightPressed;
                else if (m.x < -stickThreshold) keys[n++] = PressedKeyCode.LeftPressed;
                bank = Mathf.Abs(m.x) > stickThreshold ? Mathf.Clamp(m.x, -1f, 1f) : 0f;

                // Pedals: Q/E or the bumpers, the right stick, or mouse X (pixels gathered since the last step).
                mouseYawAccum += input.Look.x;
                pedal = input.Yaw + input.LookStick.x + Mathf.Clamp(mouseYawAccum * mouseYawGain, -1f, 1f);
                mouseYawAccum = 0f;
                pedal = Mathf.Abs(pedal) > pedalDeadzone ? Mathf.Clamp(pedal, -1f, 1f) : 0f;

                ascend = (input.Ascend ? 1f : 0f) - (input.Descend ? 1f : 0f);
            }

            // Throttle: hold the asked-for climb rate, with lift only once the rotors are up to speed.
            float target;
            float liftAvailable = Mathf.Clamp01((rotor - liftFromRotor) / Mathf.Max(0.01f, 1f - liftFromRotor));
            if (!Occupied)
            {
                commandVy = 0f;
                target = Mathf.MoveTowards(controller.EngineForce, 0f, spoolRate * dt);
            }
            else
            {
                float wantVy;
                if (ascend != 0f || controller.IsOnGround)
                {
                    holding = false;
                    wantVy = ascend > 0f ? climbRate
                        : ascend < 0f ? -Mathf.Min(descendRate, Mathf.Max(touchdownRate, Altitude * landingCushion)) : 0f;
                }
                else
                {
                    // Hands off: hold the height its momentum carries it to (no bounce back).
                    if (!holding)
                    {
                        holding = true;
                        float vy0 = body.linearVelocity.y;
                        holdY = transform.position.y + vy0 * Mathf.Abs(vy0) / (2f * Mathf.Max(0.5f, verticalBrake));
                    }
                    wantVy = Mathf.Clamp((holdY - transform.position.y) * holdGain, -descendRate, climbRate);
                    // Just off the ground (a landing, or a bounce on touchdown): settle onto the skids.
                    if (Altitude < settleHeight && body.linearVelocity.y < 1f) wantVy = -touchdownRate;
                }
                // Weight: the climb rate eases towards the one asked for; building it is slower than checking it.
                bool building = Mathf.Abs(wantVy) > Mathf.Abs(commandVy) && (Mathf.Approximately(commandVy, 0f) || Mathf.Sign(wantVy) == Mathf.Sign(commandVy));
                commandVy = Mathf.MoveTowards(commandVy, wantVy, (building ? verticalAccel : verticalBrake) * dt);
                if (controller.IsOnGround && ascend <= 0f) commandVy = 0f;
                wantVy = commandVy;
                if (!controller.IsOnGround)
                    body.AddForce(Vector3.up * ((wantVy - body.linearVelocity.y) * verticalAssist), ForceMode.Acceleration);

                float lift = 1f - Mathf.Clamp01(transform.position.y / Mathf.Max(1f, controller.EffectiveHeight));
                float upright = Mathf.Max(0.35f, Vector3.Dot(transform.up, Vector3.up));
                float hover = -Physics.gravity.y / Mathf.Max(0.15f, lift) / upright;
                float trim = (wantVy - body.linearVelocity.y) * climbGain;
                target = Mathf.Clamp(hover + trim, 0f, maxEngineForce * liftAvailable);
                // On the skids with no climb asked for: sit there at a little under hover.
                if (controller.IsOnGround && ascend <= 0f) target = Mathf.Min(target, hover * 0.92f);
                target = Mathf.MoveTowards(controller.EngineForce, target, spoolRate * 4f * dt);
            }
            controller.EngineForce = target; // the asset ties rotor speed and pitch to this...
            DriveRotors();                    // ...so set them again from the rotors' own speed

            Attitude(dt, pedal, bank);
            Inertia();
            Air();

            var send = buffers[n];
            for (int i = 0; i < n; i++) send[i] = keys[i];
            int repeats = Occupied ? responseBoost : 1;
            for (int r = 0; r < repeats; r++) controlPanel.KeyPressed(send);
        }

        private bool Flying => Occupied && !controller.IsOnGround && rotor > 0.9f;

        private float Speed01 => Mathf.Clamp01(Speed / Mathf.Max(1f, turbulenceTopSpeed));

        /// <summary>0..1 Perlin noise, centred on 0, on its own channel.</summary>
        private float Noise(float channel, float rate) => (Mathf.PerlinNoise(seed + channel * 17.3f, Time.time * rate) - 0.5f) * 2f;

        /// <summary>
        /// Turns the airframe with rotational inertia. Pitch and roll swing towards
        /// the lean the asset asks for (a damped spring: weight, a touch of
        /// overshoot); the turn rate builds and runs down; turbulence pushes on
        /// all three. The lift follows the tilt, so the air moves the helicopter.
        /// </summary>
        private void Attitude(float dt, float pedal, float bank)
        {
            bool grounded = controller.IsOnGround;
            Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float forward = Vector3.Dot(body.linearVelocity, flatFwd);
            float atSpeed = Mathf.Clamp01(forward / bankTurnSpeed);

            // Yaw: build and run down the turn rate.
            float wantYaw = grounded ? 0f : pedal * yawRate + bank * bankTurnRate * atSpeed;
            bool building = Mathf.Abs(wantYaw) > 1e-3f && (Mathf.Sign(wantYaw) != Mathf.Sign(yawVel) || Mathf.Abs(wantYaw) > Mathf.Abs(yawVel));
            float before = yawVel;
            yawVel = Mathf.MoveTowards(yawVel, wantYaw, (building ? yawAccel : yawStop) * dt);
            if (Flying) yawVel += Noise(5f, 0.45f) * yawTurbulence * (0.5f + Speed01) * dt;
            if (grounded && !Occupied) yawVel = 0f;
            float yawAcc = (yawVel - before) / dt;

            // The lean the asset asks for (x = bank right, y = nose down), plus leaning into a turn at speed.
            Vector2 t = controller.TiltTarget;
            float wantPitch = t.y - forward * flapback;
            float wantRoll = -t.x - yawVel * turnLean * atSpeed;
            tiltTarget = new Vector2(wantPitch, wantRoll);

            float w = 2f * Mathf.PI * swingFrequency;
            float c = 2f * swingDamping * w;
            float wp = 2f * Mathf.PI * pitchFrequency;
            float pitchAcc = (wantPitch - pitch) * wp * wp - pitchVel * (2f * pitchDamping * wp);
            float rollAcc = (wantRoll - roll) * w * w - rollVel * c - yawAcc * torqueReaction;
            if (Flying)
            {
                float low = 1f - Mathf.Clamp01(Altitude / 8f);
                float amp = turbulenceHover + turbulenceSpeed * Speed01 + rotorWash * low;
                // A slow wander and a quicker chop.
                // The air rocks pitch about as far as roll, though pitch is heavier to move.
                pitchAcc += (Noise(0f, 0.5f) * 0.7f + Noise(1f, 1.9f) * 0.3f) * amp * (wp * wp) / (w * w);
                rollAcc += (Noise(2f, 0.5f) * 0.7f + Noise(3f, 1.9f) * 0.3f) * amp;
            }
            pitchVel += pitchAcc * dt;
            rollVel += rollAcc * dt;
            pitch += pitchVel * dt;
            roll += rollVel * dt;
            if (grounded)
            {
                // Sitting on the skids: settle level.
                pitch = Mathf.MoveTowards(pitch, 0f, 30f * dt);
                roll = Mathf.MoveTowards(roll, 0f, 30f * dt);
                pitchVel = rollVel = 0f;
            }

            float heading = body.rotation.eulerAngles.y;
            body.rotation = Quaternion.Euler(pitch, heading, roll);
            body.angularVelocity = new Vector3(0f, yawVel * Mathf.Deg2Rad, 0f);
        }

        /// <summary>
        /// Weight: the asset's lift tilts with the airframe, so leaning pushes it
        /// along at g·tan(lean). Hold back part of that sideways push, so a big
        /// lean gathers speed slowly, and leaning back sheds it slowly too.
        /// </summary>
        private void Inertia()
        {
            if (controller.IsOnGround) return;
            float lift = controller.EngineForce * (1f - Mathf.Clamp01(transform.position.y / Mathf.Max(1f, controller.EffectiveHeight)));
            Vector3 push = Vector3.ProjectOnPlane(body.rotation * Vector3.up * lift, Vector3.up);
            body.AddForce(-push * (1f - thrustShare), ForceMode.Acceleration);
        }

        /// <summary>Slow gusts and bumps on the whole helicopter.</summary>
        private void Air()
        {
            if (!Flying) return;
            float low = 1f - Mathf.Clamp01(Altitude / 8f);
            // A light breeze that slowly swings round (the air is never quite still), with gusts on top.
            float swing = seed + Time.time * 0.05f;
            Vector3 breeze = new Vector3(Mathf.Cos(swing), 0f, Mathf.Sin(swing)) * 0.2f;
            Vector3 g = (breeze + new Vector3(Noise(6f, 0.12f) * 0.7f + Noise(10f, 0.5f) * 0.3f, 0f,
                                              Noise(7f, 0.12f) * 0.7f + Noise(11f, 0.5f) * 0.3f)) * gust * (1f + Speed01);
            g.y = (Noise(8f, 0.8f) * 0.6f + Noise(9f, 2.4f) * 0.4f) * bump * (0.5f + Speed01 + low * 0.5f);
            body.AddForce(g, ForceMode.Acceleration);
        }

        private void DriveRotors()
        {
            float spin = rotor * rotor; // slow at first, then fast
            if (controller.MainRotorController != null) controller.MainRotorController.RotarSpeed = spin * mainRotorDegPerSec;
            if (controller.SubRotorController != null) controller.SubRotorController.RotarSpeed = spin * tailRotorDegPerSec;
            if (rotorAudio != null)
            {
                float load = Mathf.Clamp((controller.EngineForce - 10f) / 30f, -0.2f, 0.4f);
                rotorAudio.pitch = Mathf.Lerp(0.3f, 1f, rotor) + load * 0.15f * rotor;
                rotorAudio.volume = rotorVolume * Mathf.Clamp01(rotor * 1.5f);
            }
        }

        private void OnCollisionEnter(Collision _) => lastContact = Time.fixedTime;

        private void OnCollisionStay(Collision _) => lastContact = Time.fixedTime;

        private float HeightAboveGround()
        {
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 300f,
                    ~(1 << gameObject.layer), QueryTriggerInteraction.Ignore))
                return Mathf.Max(0f, transform.position.y - hit.point.y);
            return transform.position.y;
        }
    }
}
