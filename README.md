# Forest Soldier — Unity prototype

Single-player third-person shooter prototype. Unity 6000.5.5f1, URP, Input System only.

Status: **Milestones 1 (soldier) and 2 (shooting range) done.** Forest and polish are Milestones 3–4.

## Open it

Unity Hub → Add → this folder. Open `Assets/_Game/Scenes/ForestRange.unity` and press Play. You spawn on the firing line facing the range.

| Action | Input |
|---|---|
| Move | W A S D (camera-relative) |
| Look | Mouse |
| Sprint | Left Shift (not while aiming) |
| Aim | Hold right mouse (zooms; turns the soldier to the camera; strafes) |
| Fire | Left mouse (automatic; hip fire turns the soldier to the camera first) |
| Reload | R (an empty trigger pull also reloads) |
| Reset targets | T |
| Pause / free the cursor | Escape; click to re-capture (that click doesn't fire) |

Jump is bound but does nothing: the soldier has no jump clip.

## The soldier asset

Source: [Soldier_Animated.blend](Tools/Blender/Soldier_Animated.blend) (a Sketchfab low-poly soldier with Mixamo clips retargeted onto it).

- **Rig:** 35-bone custom rig, imported as **Generic**. It cannot be Humanoid: the hands, feet, arm/leg poles and weapons are parented to the root bone, not to the limbs (an IK-style rig with the IK baked into the keys). No hips bone; `Spinelower` plays that part.
- **Weapons:** an SMG skinned to `SMGBone` (used) and a shotgun skinned to `SHOTGUNbone` (collapsed to zero size in every clip).
- **Scale/facing:** 4.44 Blender units tall → import scale 0.405 → 1.79 m. Faces +Z in Unity, feet at y = 0.
- **Root motion:** baked into `rootJoint` in the source; the export strips horizontal travel, so every clip is in-place and the CharacterController owns movement (Apply Root Motion is off).

### Clip mapping

| State | Clip | Notes |
|---|---|---|
| Idle / aim idle | Rifle Aiming Idle | SMG shouldered, level |
| Run forward (1.8 m/s) | Loop Run Forward | loop cut from *Shoot Rifle*, which starts from a standstill |
| Sprint (5.5 m/s) | Sprint Forward | already in place; SMG carried low in one hand |
| Aim strafe right | Loop Strafe Right | loop cut from *Strafing* (frames 33–53) |
| Aim strafe left | Loop Strafe Left | **baked**: mirrored legs, right-handed upper body kept |
| Aim walk back | Aim Walk Back | **baked**: *Backwards Rifle Walk* legs + aiming upper body (the original holds the gun low and to the left) |
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
- **The barrel follows the crosshair.** `SoldierAimRig` turns the upper body as one rigid piece about the chest (yaw, then pitch, no roll; a few passes so near aim points converge), then adds recoil. It has to move several bones together because the rig hangs the hands and SMG off the root.
- **Reload over movement.** `UpperBodyOverlay` plays *Reload(1)* on a hidden copy of the skeleton and copies its upper body onto the real one, re-seated on the real chest. This is the runtime version of what the Blender export does for the aim clips. An Avatar Mask layer can't do it on this rig.
- **Targets.** `DamageableTarget` (100 HP) on a hinge; `Hitbox` children scale damage (torso 1×, head 2× and critical). Hits flash and rock the dummy; at 0 HP it falls and its colliders turn off. T resets all of them.
- **Weapon defaults** (on `HitscanWeapon`): 600 rpm, 25 damage, 30 rounds, 2.2 s reload (ammo in at 80%), 200 m range, spread 0.2° aimed / 1.4° hip, plus movement and per-shot bloom.

The range runs east from the spawn: targets at 10, 15, 20, 25 (moving), 30, 40, 50 (half behind a crate), 60 and 75 m (moving), with distance posts every 10 m and a backstop.

## Inspector wiring

All of this is assigned by the setup script; listed here so you can check or redo it by hand.

- **Player** (layer `Player`): CharacterController (height 1.8, radius 0.3, centre y 0.9, step 0.35, slope 50°).
  - `SoldierInput.actions` → `SoldierControls`
  - `SoldierMotor.input` → the Player's SoldierInput; `cameraRig` → Main Camera's ThirdPersonCamera (set on the scene instance; it falls back to finding one); `groundMask` → everything except Player and Ignore Raycast
  - `SoldierAnimator.animator` → Model's Animator; `motor` → Player's SoldierMotor
- **Player/Model**: the FBX. Animator controller `Soldier`, Apply Root Motion off, Culling Always Animate.
- **Main Camera** (not parented to the player): `ThirdPersonCamera.target` → Player; `input` → Player's SoldierInput; `collisionMask` → everything except Player and Ignore Raycast.

Tuning lives on SoldierMotor (speeds, acceleration, turn rates, gravity) and ThirdPersonCamera (offsets, distances, FOV, sensitivity, pitch limits, collision). The run speed and blend-tree time scales are matched to the measured clip speeds, so feet don't slide. If you change `runSpeed` or `sprintSpeed`, update the matching constants in `ForestSoldierSetup` and rebuild.

## Tests

Play-mode tests drive the soldier through `SoldierInput.ExternalControl` at a fixed 60 fps.

```bash
/Applications/Unity/Hub/Editor/6000.5.5f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults Logs/playmode-results.xml
```

Prefix the command with `FOREST_SOLDIER_SHOTS=1` to also write screenshots to `Logs/Screens/`.

Milestone 1 tests: camera-relative running, sprint speed and stopping, aim-strafe facing, walls stopping both movement and the run animation, the camera not passing through a wall behind the player, and walking up a 20° ramp but not a 60° one.

Milestone 2 tests (18 in all with M1):
- **Hit resolution:** hits what the crosshair is on; low cover takes the round even when the camera sees over it; a barrel through a wall hits the wall; things between the camera and the player are ignored.
- **Firing:** an aimed burst drops the 15 m target in 8 rounds or fewer, with hit markers and the target falling over; 600 rpm and a 30-round magazine; no shots while reloading; the magazine is full afterwards; an empty trigger pull starts a reload.
- **Resetting:** reset restores health and colliders.
- **Aiming:** the barrel stays within 3° of the crosshair looking up, level and down.
- **Hip fire:** never shoots out of the side of the body.

## Milestone 2 checklist (by hand, in the Editor)

- [ ] Right-click and fire at the 10–20 m targets: muzzle flash, tracer, sparks, gunshot, hit ping, hit marker, damage numbers; the dummy rocks, and falls at 0 HP
- [ ] Headshots show yellow numbers and take two hits
- [ ] Shoot the 50 m target's lower half: the crate takes it
- [ ] Look up and down while aiming: the SMG stays on the crosshair
- [ ] Empty the magazine; R reloads (or pull the trigger on empty), with the reload animation even while walking
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

## Known gaps / next

- **Sounds and effects are placeholders** generated by the setup script (synthesised WAVs, a procedural flash texture). Drop real assets onto `WeaponEffects` to replace them.
- **Looking far down while aiming** bends the whole upper body forward about the chest; it reads more like a bow than a lean. A two-bone spread (some pitch on the hips) would look better.
- **Sprint holds the SMG one-handed and low** (that's the clip); firing cancels sprint.
- *Firing Rifle* is unused: procedural recoil reads better at 600 rpm. `UpperBodyOverlay` could play it if wanted.
- Next: Milestone 3, the forest terrain, replacing the grey test ground. The range layout moves into it.
