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
| Reset targets | T | View |
| Pause | Escape (click to resume) | Menu (press again to resume) |
| Jump | Space (no clip, does nothing) | A (same) |

Front view lets you watch the soldier fire while keeping the weapon aimed in the gameplay direction. In first person, hold Aim to look down the rifle's iron sights.


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
