# Forest Soldier — Unity prototype

Single-player third-person shooter prototype. Unity 6000.5.5f1, URP, Input System only.

Status: **Milestones 1–4 done** (soldier, shooting range, forest, polish and a macOS build), with Xbox controller support.

## Open it

Unity Hub → Add → this folder. Open `Assets/_Game/Scenes/ForestRange.unity` and press Play. You spawn on the firing line facing the range.

| Action | Keyboard & mouse | Xbox controller |
|---|---|---|
| Move | W A S D (camera-relative) | Left stick |
| Look | Mouse | Right stick |
| Sprint | Hold Left Shift | Click left stick (toggle; ends when you stop or aim) |
| Crouch | Hold Left Ctrl | B (toggle) |
| Aim | Hold right mouse | Left trigger |
| Fire | Left mouse | Right trigger |
| Toggle front view | V | Left Bumper |
| Toggle first person | C | Click right stick |
| Reload | R | X |
| Select weapon | 1–4 | Y / Triangle cycles weapons |
| Reset targets | T | View |
| Pause | Escape (click to resume) | Menu (press again to resume) |
| Jump | Space (no clip, does nothing) | A (same) |

Front view lets you watch the soldier fire while keeping the weapon aimed in the gameplay direction. In first person, hold Aim to look down the rifle's iron sights.

Weapon slots are 1 Soldier SMG, 2 PP-19 Bizon, 3 PP-19-01, and 4 FAMAS F1. Each keeps its own magazine count. Switching weapons cancels the current reload. The three added models share the prototype's firing rate, magazine capacity, sounds and reload animation, with separate muzzle, sight, palm-grip and stock contacts. The stocks seat at the right shoulder; both arms refit to the grips, including the PP-19-01's vertical foregrip and the Bizon's launcher underneath. The imported lengths are 0.66 m (Bizon), 0.69 m (PP-19-01), and 0.757 m (FAMAS). Their proportions are preserved. The original suppressed gun retains its animated firing-hand pose; only its support hand is refitted around the barrel. Imported stocks seat at the shoulder in all three axes. The FAMAS sight marker is measured at its rear aperture. First-person shots follow the displayed camera after the weapon pose; the crosshair remains visible while using the sights.


## The soldier asset

Source: [Soldier_Animated.blend](Tools/Blender/Soldier_Animated.blend) (a Sketchfab low-poly soldier with Mixamo clips retargeted onto it).

- **Rig:** 35-bone custom rig, imported as **Generic**. It cannot be Humanoid: the hands, feet, arm/leg poles and weapons are parented to the root bone, not to the limbs (an IK-style rig with the IK baked into the keys). No hips bone; `Spinelower` plays that part.
- **Weapons:** an SMG skinned to `SMGBone` (used) and a shotgun skinned to `SHOTGUNbone` (collapsed to zero size in every clip).
- **Scale/facing:** 4.44 Blender units tall → import scale 0.405 → 1.79 m. Faces +Z in Unity, feet at y = 0.
- **Root motion:** baked into `rootJoint` in the source; the export strips horizontal travel, so every clip is in-place and the CharacterController owns movement (Apply Root Motion is off).

### Clip mapping

| State | Clip | Notes |
|---|---|---|
| Idle | Rifle Aiming Idle | Standing base pose |
| Standing aim and fire | Rifle Aim Idle | Upper body follows the crosshair |
| Crouch aim and fire | Idle Crouching Aiming | Upper body blends over Crouch Walking while moving |
| Walk forward (0.99 m/s) | Walking | full forward walk cycle |
| Move forward while aiming (1.8 m/s) | Loop Run Forward | full rifle run cycle from *Shoot Rifle* |
| Sprint (5.5 m/s) | Sprint Forward | in-place cycle with a two-handed low-ready pose |
| Aim strafe right | Loop Strafe Right | loop cut from *Strafing* (frames 33–53) |
| Aim strafe left | Loop Strafe Left | **baked**: mirrored legs, right-handed upper body kept |
| Aim walk back | Walking Backwards | backward walk with the rifle aim pose blended over it |
| Aim diagonals forward | Aim Walk Forward Left / Right | **baked**: *Walk Forward Left* legs (mirrored for right) + aiming upper body (the original is at port arms) |
| Fire | — | procedural recoil instead of *Firing Rifle*, so the kick matches the fire rate |
| Reload | Reload(1) | standing reload, played over the legs by `UpperBodyOverlay`; *Reload* is a 207-frame kneeling one |

**Missing from the source:** jump (so Space does nothing; deferred per the plan), backward diagonals (the blend tree interpolates them), a left strafe and a forward-right walk (baked by mirroring as above). Unused but available: crouch walks/turns, two deaths, turn-in-place clips.

The model also contains three previewable locomotion loops: `Walking`, `Walking Backwards`, and `Crouch Walking`. Expand `Soldier.fbx` in the Unity Project window to select a clip.

### Re-exporting

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b Tools/Blender/Soldier_Animated.blend --python Tools/Blender/export_soldier.py -- /tmp/soldier_export
```

Then copy `Soldier.fbx` into `Assets/_Game/Art/Characters/Soldier/` and run **Forest Soldier → Rebuild Milestone 1 Assets**. The script never modifies the .blend. `Tools/Blender/last_export_report.json` records loop frames, measured clip speeds and the hand-placement check for the baked clips.

## What's in the project

```
Assets/_Game/
  Art/Characters/Soldier/   Soldier.fbx, Textures/
  Animations/               Soldier.controller (one 2D freeform blend tree, MoveX/MoveZ in m/s)
                            SoldierOverlay.controller (reload, played on a hidden copy of the rig)
  Art/Weapons/              generated muzzle-flash and soft-dot textures
  Audio/                    generated placeholder sounds: gunshot, dry fire, reload clack, hit confirm
  Input/                    SoldierControls.inputactions
  Prefabs/Player/           Player.prefab, ImpactBurst.prefab
  Prefabs/Targets/          TargetDummy.prefab
  Scenes/                   ForestRange.unity (test ground + shooting range)
  Scripts/Player/           SoldierInput, SoldierMotor, SoldierAnimator, ThirdPersonCamera,
                            SoldierAimRig, UpperBodyOverlay
  Scripts/Combat/           HitscanWeapon, HitscanResolver, WeaponEffects, IDamageable,
                            DamageableTarget, Hitbox, TargetMover
  Scripts/UI/               PrototypeHUD, TargetResetController
  Editor/                   ForestSoldierSetup (builds the controller, prefab and scene)
  Tests/PlayMode/           SoldierMilestone1Tests, SoldierMilestone2Tests
Tools/Blender/              export_soldier.py
```

`ForestSoldierSetup.BuildAll` (menu **Forest Soldier → Rebuild Prototype Assets**) regenerates both controllers, the Player, TargetDummy and ImpactBurst prefabs, the generated sounds and textures, and `ForestRange.unity`. If you hand-edit any of those, don't run it again (or copy them first). Close the Editor before running it or the tests from the command line.

## How shooting works

- **Aim from the camera, fire from the gun.** The crosshair ray (starting level with the soldier, so nothing between camera and player counts) picks the aim point. The round then travels from the muzzle to that point, so low cover in front of the gun takes the hit even when the camera sees over it. If the barrel is through a wall (shoulder → muzzle blocked), the wall takes it. All of this lives in `HitscanResolver`, which is plain physics queries.
- **The rifle sights sit at the right eye.** `SoldierAimRig` turns the torso toward the crosshair, levels the rifle using the axes measured from its mesh, and moves both hands with the rifle. Recoil pivots around the eye after the grip is set.
- **Reload over movement.** `UpperBodyOverlay` plays *Reload(1)* on a hidden copy of the skeleton and copies its upper body onto the real one, re-seated on the real chest. This is the runtime version of what the Blender export does for the aim clips. An Avatar Mask layer can't do it on this rig.
- **Targets.** `DamageableTarget` (100 HP) on a hinge; `Hitbox` children scale damage (torso 1×, head 2× and critical). Hits flash and rock the dummy; at 0 HP it falls and its colliders turn off. T resets all of them.
- **Weapon defaults** (on `HitscanWeapon`): 600 rpm, 25 damage, 30 rounds, 2.2 s reload (ammo in at 80%), 200 m range, spread 0.2° aimed / 1.4° hip, plus movement and per-shot bloom.

The range runs east from the spawn: targets at 10, 15, 20, 25 (moving), 30, 40, 50 (half behind a crate), 60 and 75 m (moving), with distance posts every 10 m and a backstop.

## The forest (Milestone 3)

`ForestRange.unity` is a 300 m Unity Terrain built by `ForestSoldierSetup.Forest.cs`. Everything is seeded, so a rebuild gives the same forest.

- **Clearings:** the training yard (x −30..30, z −35..30) and the range lane (x 10..110, z ±12) are flat at y = 0 and gravelled. They blend into rolling forest floor (about ±5 m) over 14 m.
- **Trail:** a dirt trail leaves the yard north, loops west past the lookout hill and the pond, and reaches the range from the south. Its grade is smoothed so it stays walkable.
- **Trees, rocks, bushes:** 573 low-poly trees (4 pine and 3 broadleaf variants), 149 rocks, 260 bushes. The meshes are generated and flat-shaded. Trunks (capsules) and rocks (convex mesh colliders) block movement and bullets. Canopies and bushes don't. Trees are kept 4 m from the clearings and off the trail.
- **Landmarks:** a fire lookout tower on the north-west hill (above the treeline), a radio mast with a red light north of the yard, three standing stones south-east, and a pond south-west.
- **Edges:** rim hills plus invisible walls at ±132 m.
- **Atmosphere:** warm sun with 90 m shadows, a procedural sky, exponential fog, the template's post-processing profile, and a generated 24 s forest ambience loop (wind, leaves, birds).
- **Layout data:** clearings, trail, pond and landmarks are defined in `Scripts/Environment/ForestLayout.cs`, which both the builder and the tests read.

Milestone 3 tests:
- **Ground:** clearings are flat and the forest rolls; the spawn is grounded.
- **Trail:** walkable end to end, at at least 70% of flat-ground speed.
- **Placement:** trees, rocks and bushes sit on the ground.
- **Blocking:** trees stop the player and bullets; the boundary holds.
- **Range:** every target's head is visible from the firing line.
- **Audio:** the ambience loops.

## Controller (Milestone 4)

- **Bindings:** the gamepad bindings are in `SoldierControls.inputactions` (Gamepad control scheme) next to keyboard and mouse. Both work at once, and the HUD prompts follow whichever you used last (`R` / `(X)`, `T` / `View`).
- **Stick look** (`ThirdPersonCamera`, "Look (gamepad right stick)"): 200°/s yaw and 140°/s pitch at full tilt, with a response curve (exponent 1.8) for fine aim. After a quarter-second at full tilt, turning speeds up by 60%. Stick look slows to 55% while the crosshair is over a live target, which is the only aim assistance. Aiming scales it like the mouse.
- **Rumble** (`GamepadRumble`): a light pulse per shot and a heavier one per kill, only while you're on the controller. The motors stop on pause or when the window loses focus.
- **Tests:** these use a virtual gamepad through the real Input System. I haven't tried a physical Xbox pad. If yours doesn't respond on macOS, check that it's paired (Bluetooth) and that the game window has focus.

## Polish, build and profiling (Milestone 4)

- **Canopies fade for the camera:** canopies and bushes carry trigger volumes on a `Foliage` layer that nothing collides with. `FoliageFader` on the camera sweeps a capsule from the camera to the player's chest and swaps the foliage in it to a transparent twin until it's clear.
- **No per-frame garbage:** the HUD no longer builds strings every frame, and target counting no longer boxes an enumerator. In the built player, steady play allocates nothing; the only allocations are damage-number strings on hits.
- **Build:** **Forest Soldier → Build macOS**, or from the command line:
  ```bash
  /Applications/Unity/Hub/Editor/6000.5.5f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -executeMethod ForestSoldier.EditorTools.ForestSoldierBuild.BuildMac
  ```
  It writes `Builds/macOS/ForestSoldier.app` (git-ignored, about 134 MB) as a 1600×900 resizable window.
- **Benchmark:** launch the built app with `-benchmark` and it plays a fixed route (firing line, shooting, the yard, the trail, a spin in dense forest), writes a JSON report and quits:
  ```bash
  "Builds/macOS/ForestSoldier.app/Contents/MacOS/Forest Soldier" -benchmark -benchmarkOut /tmp/bench.json -screen-width 1920 -screen-height 1080 -screen-fullscreen 0
  ```
  Results on an M2 Max at 1920×1080, vsync off (`Tools/last_benchmark_m2max.json`):

  | Phase | Frame avg | p99 | CPU avg | GPU avg |
  |---|---|---|---|---|
  | Firing line | 1.6 ms | 6.1 ms | 1.6 ms | 2.9 ms |
  | Shooting | 1.6 ms | 2.5 ms | 1.6 ms | 3.0 ms |
  | Yard sprint | 1.5 ms | 2.4 ms | 1.5 ms | 2.5 ms |
  | Trail walk | 1.6 ms | 2.5 ms | 1.6 ms | 3.0 ms |
  | Forest spin | 1.9 ms | 2.6 ms | 1.9 ms | 3.8 ms |

  The GPU time is above the frame time because the CPU and GPU work on different frames in parallel.

Milestone 4 tests:
- **Accuracy:** aimed shots land within the spread cone at 10 m and 60 m and hit the target aimed at; point blank hits the bench.
- **Canopy fading:** canopies fade around the camera and come back.
- **Garbage guard:** moving, aiming and firing add under 128 B a frame over the editor's idle baseline.
- **Controller (7 tests):** stick movement and look, the response curve, the triggers, the sprint and crouch toggles, Menu pause, View reset, X reload, and prompts following the last device.

The whole suite is 38 tests: 35 run, and the 3 screenshot tests need `FOREST_SOLDIER_SHOTS=1`.

## Destructible houses, grenade launcher, casings

**Houses (BattleBit-style destruction).**
- **Where:** four houses stand in the **hamlet**, a clearing on the west trail (`ForestLayout.Hamlet`).
- **How each is built:** `ForestSoldierSetup.Destruction` pre-fractures every house into 165–190 staggered wall blocks and roof slabs. All of them go into **one skinned mesh** with one bone per fragment (weight 1), so a house is one renderer, one material and one draw call, intact or in pieces. All four share a single palette-texture material.
- **What a blast does:** `DestructibleStructure.ExplodeAt` detaches the fragments within 55% of the blast radius. Each detached fragment gets a Rigidbody and an explosion impulse. A neighbour graph (built in the editor) then finds anything no longer connected to the ground, and that falls too, so knocking out walls drops the roof.
- **Cleanup:** debris that has come to rest (or after 20 s) loses its Rigidbody and stays as static rubble. While pieces fly, the mesh's bounds are updated by hand, so it never needs `updateWhenOffscreen`.
- **Reset:** T / View rebuilds every house.

**Yard and range structures.** The same system (`FractureBlock` + `BuildFractured`) turns the training yard's three walls, its eight pillars, the low roof with its two posts, and the range's firing bench, cover wall, cover crate and backstop into destructible structures:
- **In place:** after the scene is built they're swapped one for one, keeping their names.
- **Sizes:** each is one skinned mesh, from 14 fragments (bench) to 347 (backstop).
- **The low roof:** it and its posts are one structure, so blowing the posts drops the roof.
- **Left solid:** the ramps and steps, which the movement tests rely on.

**Grenade launcher (the Bizon's under-barrel tube, 40 mm).**
- **Switch to it:** **G** on the keyboard, **RB** on the controller. Fire launches a round; it reloads itself from 8 spare (shown as `1 / 8  40mm`).
- **Where it fits:** there's no added model. Rounds leave from the front of the tube already under the Bizon's barrel (the model's `Mag` part), 6 cm behind and 3.7 cm below its `Muzzle` marker (`GrenadeLauncher.tubeOffset`). It's available only while a loadout weapon with "Bizon" in its name is selected; switching weapons puts it away.
- **Aiming:** the arc is solved so the round lands on the crosshair (the low arc if in range, otherwise 45°).
- **Flight:** rounds are simulated as a sphere cast along the arc each frame, with no rigidbodies, so they can't tunnel. They arm after 6 m; a hit before that is a dud.
- **The blast:** 7 m radius, up to 220 damage with a steep falloff. Walls shield whoever is behind them. It breaks houses, pushes loose debris, and comes with fire, smoke, sparks, dirt, a light flash, a 3D boom, and camera shake (`CameraShake`).

**Ragdolls (blast deaths).**
- **When it happens:** a walker killed by a grenade goes limp instead of playing a death animation. `WalkerRagdoll` adds 11 rigidbodies with capsule or sphere colliders and character joints (pelvis, chest, head, upper and lower arms and legs). It then throws them away from the blast and up, scaled by distance, with spin.
- **The rig problem:** the rig hangs the hands, feet, IK poles and gun off the root bone, so for the ragdoll they're re-parented onto the forearms, shins and right hand. Everything is recorded and put back exactly on reset.
- **Settling:** after 8 s a body that has stopped moving goes kinematic, so a pile of bodies costs no physics.
- **Bullet kills** still play "Dying" or "Death From Back Headshot".
- **Reset:** T / View stands everyone back up and walking.

**Bigger explosions.**
- **What's in each blast:** on top of the fireball, smoke, sparks and dirt, there's a two-frame core flash, a flat shockwave ring racing out along the ground, a low dust ring that slows as it spreads, glowing embers on short trails, rock chunks that bounce and settle (mesh particles with world collision), and a dark smoke column that keeps rising for 5–8 s.
- **Scorch marks:** each blast on the ground leaves a scorch mark that fades over 45 s. Reset wipes them.

**Bullet travel.**
- **The streak:** every round draws a glowing streak (`WeaponEffects`, "Bullet travel") that flies from the muzzle to the hit at 350 m/s (`bulletSpeed`), 4.5 m long and 5 cm wide.
- **Timing:** the sparks or blood appear when the streak arrives. Damage, hit markers and sounds are still instant.
- **Glow:** the tracer material has an HDR tint, so bloom catches it.

**Shell casings.**
- **One particle system:** every casing is a particle in a single mesh-particle system, one GPU-instanced draw, with no Rigidbodies or GameObjects. Each shot emits one from the ejection port with spin and sideways velocity.
- **Collision:** the particle system's own world collision (High quality, about a 7 mm radius) bounces them off the ground.
- **Lifetime:** they lie where they land for 2 minutes, or until the 400-casing ring buffer recycles the oldest.

Tests: `SoldierDestructionTests` (16).
- Each house is one skinned mesh with a bone per fragment.
- A blast breaks only the wall near it, and nothing attached is left floating.
- Knocking out the walls drops the roof.
- Debris freezes into rubble and reset rebuilds it.
- The launch solver lands within 0.3 m.
- A launcher round blows a hole where you aim, the rifle stays quiet in launcher mode, and the launcher reloads.
- A blast kills a nearby walker, but a wall shields one indoors.
- Casings come to rest on the ground, all in one particle system.
- The yard and range structures are destructible: a blast opens the yard wall, and taking out the posts drops the low roof.
- Bullets travel: damage is instant, but the impact waits until the round gets there.
- A blast death ragdolls: the body is thrown up and away, lands, and reset restores the walker.
- Bullet kills still play the death animations.
- Every explosion layer fires, it scorches the ground, the rocks land on the ground, and reset clears the scorches.

## Little Bird helicopter

An MH-6 Little Bird sits on a helipad beside the range (16, 0, 7.5). Walk up to it and press **F** (controller: **Y** or **D-pad up**) to get in. Press the same button again to get out beside the door. The rotors take 4 s to run up (the HUD shows their speed), and it won't lift until they're nearly there.

| | Keyboard and mouse | Xbox controller |
|---|---|---|
| Get in / out | F | Y or D-pad up |
| Pitch and bank (fly) | WASD | Left stick |
| Turn (pedals) | Q / E or mouse X | LB / RB or right stick X |
| Look up and down | Mouse Y | Right stick Y |
| Climb / descend | Space / Left Ctrl | A / B |
| Miniguns | Left mouse | RT |
| Rockets (rippled while held) | Right mouse | LT |

On foot, Y also switches weapons. A press that gets you in doesn't also switch.

**Flight** is the Base Helicopter Controller asset (`Assets/Helicopter`): its `HelicopterController` lift, tilt and forward thrust, and its `HeliRotorController` rotor spinners. Our `HelicopterPilot` (`Scripts/Vehicles`) sits on top of it. It aims for weight and momentum, and it is never quite still in the air.
- **Input:** the asset's `ControlPanel` reads the legacy Input class, which this project has switched off. So the panel stays disabled, and the pilot raises its `KeyPressed` event from the Input System, twice per physics step. The pedals are analog: stick, bumpers or mouse, with small inputs giving small turns.
- **Attitude:** the asset works out how far to lean (40° nose down, so the guns reach targets below; 30° into a bank; `TiltResponse` 2, so the lean builds over about a second) but no longer sets the rotation (`ExternalAttitude`). The pilot turns the airframe with rotational inertia. Pitch and roll swing towards the lean like a mass on a spring, slightly under-damped, so they take a moment and overshoot a little. Pitch is heavier than roll (0.45 swings a second against 0.8), so tipping into a full lean or levelling out takes about 1.5–2 s.
- **Flapback:** airspeed lifts the nose, 0.06° per m/s. Let go at speed and the nose comes up, then eases down as it slows.
- **Turning:** the turn rate builds at 150°/s² up to 75°/s, and runs down at 80°/s², so the nose carries on after you let go. At speed it leans into the turn, and starting or stopping a turn kicks a little roll from the tail rotor. Banking at speed turns the nose at up to 35°/s.
- **Momentum:** it leans a lot, but only 56% of the leaning lift's sideways push moves it (`thrustShare`, set so a full 40° lean accelerates like a full 32° one at 75%), and there's almost no drag (0.13) and no push along the nose. So a big lean gathers speed over seconds (about 50–70 km/h after 6 s, 125 km/h flat out), it coasts a long way when you let go, and leaning back takes a few seconds to stop it. The chase camera trails it a little and widens at speed, so you see it gather and shed speed.
- **Air:**
  - Turbulence pushes on pitch, roll and yaw: noticeable in a hover, and much stronger at speed and in its own rotor wash near the ground. Gusts push twice as hard at top speed.
  - The lift follows the tilt, so the rocking nudges it about, and slow gusts and bumps push the whole helicopter a little.
  - A light breeze slowly swings round under the gusts, so the air is never quite still.
  - Hands off in a hover, it rocks 3–7° and drifts 4–10 m in 5 s.
- **Settings:** all of these are on `HelicopterPilot`, under **Handling**, **Weight (attitude)** and **Air**.
- **Throttle:** set every step to fly a climb rate, up to 8 m/s up and 12 m/s down.
  - The collective has weight: the climb or sink rate builds at 4 m/s² and is checked at 6 m/s².
  - Let go and it carries on a little, then holds the height its momentum takes it to, with no bounce back.
  - Near the ground the descent eases off (0.8 m/s per metre of height, down to 2.5 m/s), so it touches down gently.
  - Hands off below 1.2 m, it settles onto the skids.
  - The asset's on-ground flag is replaced by a contact check, because one skid lifting used to clear it while the other still rested.
- **Rotors:** they have their own speed, separate from the throttle, so they never slow while you descend. They run up over 4 s, slow at first and then fast, and the sound rises with them. After you get out, they run down over about 9 s.

**Camera:** `HelicopterCamera` on the main camera, a chase camera that takes over from the on-foot rig while you fly.

**HUD and targeting** (`HelicopterHUD`, drawn as green vector lines by `UI/HudLines` in one mesh). The soldier's HUD hides while you fly.
- **Heading tape** across the top, with the heading readout.
- **Speed** (km/h) on the left. **Height** on the right, with the climb rate and a 0–60 m radar-altitude scale.
- **Attitude indicator** centred on the helicopter. A pitch ladder from −40° to +40°, numbered every 10°, runs either side of the body, with a gap as wide as the cabin and pods on screen, so it never covers the helicopter. Two short bars beside it mark its own level. It banks the same way the helicopter does at half the angle; 4.5 px per degree, within 190 px. Gap, line length and scale are settings on `HelicopterHUD`.
- **Gun reticle:** a circle where the rounds will actually land, which slides off the crosshair once the target is past the guns' 25° swing, plus a spin-up ring. It flashes on a hit and turns red on a kill, with "KILL" below.
- **Rocket marker:** a diamond where a rocket fired now would land, worked out along its dropping path.
- **Targets:** a red diamond over every live target and walker within 400 m. The locked target gets pulsing brackets, LOCK and its range.
- **Weapons panel:** minigun spin-up, 14 rocket pips, and REARMING while the pods refill.
- **Warnings:** rotor spool percentage while the rotors run up, and PULL UP when sinking fast low over the ground.
- **Controls hint:** bottom left, for keyboard or controller.

**Weapons** (`HelicopterWeapons`):
- **Miniguns:** two of them, 2400 rpm hitscan from alternating barrels after a 0.25 s spin-up. They swing up to 25° off the nose to meet the crosshair, or the locked target. Streaks, impacts, blood, casings and hit markers come from the soldier's own `WeaponEffects` and HUD.
- **Rockets:** 14 unguided rockets in two pods. Each one bursts with the grenade's blast, so it does damage, destruction and ragdolls, only harder.
  - It breaks walls over 1.7× a grenade's reach (`breach` on `Explosions.Explode`; grenades keep 1) and throws the pieces harder (force 13).
  - It tears about 12 chunks off whatever it hits (`Combat/DebrisBurst`, a pool of 120 physics blocks), up to 16 more off a structure it breaks. Chunks take the surface's own material (wall, rock, or dirt off the ground), tumble, lie for 7 s and shrink away. They're on Ignore Raycast, so rounds and blasts pass through them.
  - The pods refill after 1.5 s parked on the ground.
- **Target lock:**
  - A live target or walker within 5° of the crosshair, within 350 m and in sight, is locked.
  - The lock holds until the target is 10° off the crosshair, and moves to another target that is clearly (2°) closer to it.
  - While locked, the guns aim at the target (the middle of a range target's board, a walker's chest), so you can keep firing on it while the helicopter rocks.

**Model:** `Tools/Blender/import_helicopter.py` converts the downloaded `helicopters_mh-6_little_bird.glb` to `Art/Vehicles/LittleBird/LittleBird.fbx` and names the rotors. The setup script builds the `LittleBird` prefab: it points the nose to +Z, adds box colliders and puts everything on the `Vehicle` layer (11). It also adds the gun and rocket mounts, measured from the weapon pods.

**Setting up the Base Helicopter Controller.** It isn't in this repository: Asset Store packages can't be shared publicly. To build the helicopter:
1. Import the free **Base Helicopter Controller** from the Asset Store into `Assets/Helicopter`.
2. Make the changes below. Each one is marked `ForestSoldier:` in our copy.
3. Run **Forest Soldier ▸ Rebuild Prototype Assets**, or `ForestSoldierSetup.BuildAll` in batch mode. Until the asset is imported, the project won't compile: our scripts reference it.

The changes:
- **`Assets/Helicopter/Scripts/BaseHelicopterController.asmdef`**, new, so our assemblies can reference the asset:
  ```json
  { "name": "BaseHelicopterController", "references": ["UnityEngine.UI"], "autoReferenced": true }
  ```
- **`CheckpointController.cs`:** comment out `using System.Security.Policy;`. It's unused and doesn't exist in this .NET profile.
- **`HelicopterController.cs`, `EngineForce` setter:** guard the sample UI, which our scene doesn't have:
  ```csharp
  if (UIGameController.runtime != null && UIGameController.runtime.EngineForceView != null)
      UIGameController.runtime.EngineForceView.text = ...;
  ```
- **`HelicopterController.cs`, new fields** after `turnForcePercent`:
  ```csharp
  public float TiltResponse = 1f;          // how quickly the tilt follows the stick
  public bool ExternalAttitude = false;    // HelicopterPilot turns the airframe itself
  public Vector2 TiltTarget { get { return hTilt; } }
  ```
- **`HelicopterController.cs`, `TiltProcess`:** use `TiltResponse`, and read and write the Rigidbody's rotation, not the Transform's. With interpolation on, writing the lagging Transform back every step undid the yaw.
  ```csharp
  hTilt.x = Mathf.Lerp(hTilt.x, hMove.x * TurnTiltForce, Time.deltaTime * TiltResponse);
  hTilt.y = Mathf.Lerp(hTilt.y, hMove.y * ForwardTiltForce, Time.deltaTime * TiltResponse);
  if (!ExternalAttitude)
      HelicopterModel.rotation = Quaternion.Euler(hTilt.y, HelicopterModel.rotation.eulerAngles.y, -hTilt.x);
  ```

**Credits.** The helicopter model is [Helicopters MH-6 Little Bird](https://sketchfab.com/3d-models/helicopters-mh-6-little-bird-69cb05db3fc3483cb729734262abf28e) by [mario low poly](https://sketchfab.com/mariogpymario117), licensed under [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/). It was converted to FBX and its rotors were renamed by `Tools/Blender/import_helicopter.py`.

Tests: `SoldierHelicopterTests` (13).
- The helicopter is built on the asset.
- Getting in and out.
- Take-off and descent build up rather than snap. Let go and it carries on up, then holds its height (the air still rocks it by more than 1°).
- The rotors run up slowly, give no lift before they're up, keep their speed while descending, and run down slowly after you get out.
- Weight and momentum:
  - The turn builds, then coasts on more than 15° after you let go.
  - The lean swings in rather than snapping (still tipping over after 1 s), it gathers way slowly (under 36 km/h after 2.5 s), and the nose comes back to level when you let go.
  - It reaches 36–137 km/h after 6 s.
  - It keeps over 70% of its speed for 2 s hands off, and leaning back takes more than 1 s (and under 6 s) to brake it.
- The air: hands off, it rocks 2.5–20° and is pushed more than 1 m (and under 25 m) in 5 s.
- HUD: drawn only while flying, with the soldier's HUD hidden; gun reticle, rocket marker and target markers on screen; the target under the crosshair locks; it all goes when you get out.
- A rocket breaks more of a wall than a grenade-style blast in the same spot, and rockets throw debris.
- With the crosshair held 4° above a locked target, the guns still hit it and cut it down.
- Controller: every gamepad binding, the right stick turning it, and controller hints on the HUD.
- The miniguns cut down the 20 m target, steering against the air like a player would.
- Rockets ripple, each explodes, it touches down at under 4 m/s, and the pods rearm on landing.
- Screenshots `h_01`–`h_05`.

## Inspector wiring

All of this is assigned by the setup script; listed here so you can check or redo it by hand.

- **Player** (layer `Player`): CharacterController (height 1.8, radius 0.3, centre y 0.9, step 0.35, slope 50°).
  - `SoldierInput.actions` → `SoldierControls`
  - `SoldierMotor.input` → the Player's SoldierInput; `cameraRig` → Main Camera's ThirdPersonCamera (set on the scene instance; it falls back to finding one); `groundMask` → everything except Player and Ignore Raycast
  - `SoldierAnimator.animator` → Model's Animator; `motor` → Player's SoldierMotor
- **Player/Model**: the FBX. Animator controller `Soldier`, Apply Root Motion off, Culling Always Animate.
- **Main Camera** (not parented to the player): `ThirdPersonCamera.target` → Player; `input` → Player's SoldierInput; `collisionMask` → everything except Player and Ignore Raycast.

Tuning lives on SoldierMotor (speeds, acceleration, turn rates, gravity) and ThirdPersonCamera (offsets, distances, FOV, sensitivity, pitch limits, collision). The walk uses its measured travel speed. If you change `aimMoveSpeed` or `sprintSpeed`, update the corresponding blend tree speeds in `ForestSoldierSetup` and rebuild.

## Tests

Play-mode tests drive the soldier through `SoldierInput.ExternalControl` at a fixed 60 fps.

```bash
/Applications/Unity/Hub/Editor/6000.5.5f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults Logs/playmode-results.xml
```

Prefix the command with `FOREST_SOLDIER_SHOTS=1` to also write screenshots to `Logs/Screens/`.

Milestone 1 tests: camera-relative running, sprint speed and stopping, aim-strafe facing, walls stopping both movement and the run animation, the camera not passing through a wall behind the player, and walking up a 20° ramp but not a 60° one.

Milestone 2 tests (18 in all with M1):
- **Hit resolution:** hits what the crosshair is on; low cover takes the round even when the camera sees over it; a barrel through a wall hits the wall; things between the camera and the player are ignored.
- **Firing:** an aimed burst drops the 15 m target in 8 rounds or fewer, with hit markers and the target falling over. The rifle fires at 600 rpm, varies its shot sample, and reloads after the last round of its 30-round magazine.
- **Resetting:** reset restores health and colliders.
- **Aiming:** the barrel stays within 3° of the crosshair looking up, level and down.
- **Hip fire:** never shoots out of the side of the body.

## Milestone 2 checklist (by hand, in the Editor)

- [ ] Right-click and fire at the 10–20 m targets: muzzle flash, tracer, sparks, gunshot, hit ping, hit marker, damage numbers; the dummy rocks, and falls at 0 HP
- [ ] Headshots show yellow numbers and take two hits
- [ ] Shoot the 50 m target's lower half: the crate takes it
- [ ] Look up and down while aiming: the SMG stays on the crosshair
- [ ] Empty the magazine to start the reload animation and sound automatically, or press R to reload early
- [ ] T stands every target back up; the top-left counter resets
- [ ] Escape, then click back in: that click doesn't fire

## Milestone 1 checklist (by hand, in the Editor)

- [ ] WASD moves relative to the camera; the soldier turns smoothly to face movement
- [ ] Shift sprints; releasing settles to idle without a pop
- [ ] Hold right mouse: camera zooms over the right shoulder, the soldier faces the crosshair direction, A/D strafe, S walks back, all with the SMG raised
- [ ] Run into a wall: the soldier stops and the legs stop
- [ ] Back into the corner walls (west) and under the blue roof: the camera pulls in and doesn't show through
- [ ] Walk up the 20° and 40° ramps and the steps; the 60° ramp blocks you
- [ ] Escape frees the cursor; clicking re-captures it

## Milestone 3 checklist (by hand, in the Editor)

- [ ] Follow the trail from the yard's north side all the way round to the range
- [ ] Climb toward the lookout tower; find the pond and the three stones
- [ ] Trees and rocks stop you; bushes don't
- [ ] Shoot a trunk: sparks on the bark, and the round doesn't pass through
- [ ] The fog hides the world's edge; the rim hills and walls keep you in
- [ ] Ambience plays under the gunfire

## Milestone 4 checklist (by hand)

- [ ] Plug in or pair the Xbox controller; walk, look, aim and fire with it; feel the rumble
- [ ] Click the left stick to sprint, B to crouch, Menu to pause and resume
- [ ] Switch between mouse and controller: the prompts follow
- [ ] Walk through dense pines: canopies near the camera turn see-through
- [ ] Open `Builds/macOS/ForestSoldier.app` and play a round outside the editor

## Known gaps / next

- **Sounds and effects are placeholders** generated by the setup script (synthesised WAVs, a procedural flash texture). Drop real assets onto `WeaponEffects` to replace them.
- **Looking far down while aiming** bends the whole upper body forward about the chest; it reads more like a bow than a lean. A two-bone spread (some pitch on the hips) would look better.
- **Grass footsteps** use separate walk and run recordings and follow the foot contacts in the locomotion clips.
- *Firing Rifle* is unused: procedural recoil reads better at 600 rpm. `UpperBodyOverlay` could play it if wanted.
- **First-sight hitches:** the benchmark shows single ~200 ms frames when it teleports into an area it hasn't drawn yet, most likely Metal compiling pipeline states the first time something is drawn. Normal play walks in gradually, but expect a one-off stutter. Warming them up at load (Unity 6's GraphicsStateCollection, or a ShaderVariantCollection) would fix it.
- **The editor's garbage numbers include its own per-frame overhead** (about 300 B idle). Trust the built player's benchmark report.
- Not done in the plan's scope: multiplayer, AI, jump (no clip).
