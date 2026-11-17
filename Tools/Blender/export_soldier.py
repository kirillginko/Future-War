"""Export the animated soldier from Soldier_Animated.blend to a Unity-ready FBX.

Run headless (does not modify the source .blend):
  Blender -b <Soldier_Animated.blend> --python Tools/Blender/export_soldier.py -- <out_dir>

What it does, in order:
  1. Renames bones to drop the glTF "_N" suffixes (ArmUpper.L_3 -> ArmUpper.L).
  2. Renames actions to drop the "Mixamo - " prefix.
  3. Cuts steady loops out of locomotion clips that start from a standstill
     ("Loop ..." clips), and bakes the clips the rig lacks or holds the gun
     wrong in: Loop Strafe Left (mirrored legs) and Aim Walk Back / Aim Walk
     Forward Left / Aim Walk Forward Right (aiming upper body on walking legs).
  4. Measures each clip's travel speed (metres/second after the Unity scale).
  5. Removes horizontal travel from the root (in-place clips; vertical bob kept).
  6. Exports armature + skinned meshes as FBX, every action as its own take,
     and writes the two packed textures as PNG.
"""
import bpy, json, os, re, sys, mathutils

OUT = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else os.getcwd()
os.makedirs(OUT, exist_ok=True)
UNITY_SCALE = 0.405  # model is 4.44 Blender units tall -> ~1.8 m
REST = 0.0  # rootJoint rest location (x and z) is the origin
FPS = bpy.context.scene.render.fps
EXPORT_FPS = 48

arm = bpy.data.objects["GLTF_created_0"]
arm.name = "Soldier"
arm.data.name = "SoldierRig"

# 1. bone renames ------------------------------------------------------------
renames = {}
for b in list(arm.data.bones):
    new = "rootJoint" if b.name == "GLTF_created_0_rootJoint" else re.sub(r"_\d+$", "", b.name)
    renames[b.name] = new
for old, new in renames.items():
    arm.data.bones[old].name = new


def channelbag(action):
    return action.layers[0].strips[0].channelbags[0]


for a in bpy.data.actions:
    for fc in channelbag(a).fcurves:
        m = re.match(r'pose\.bones\["(.+?)"\]\.(.+)', fc.data_path)
        if m and m.group(1) in renames:
            fc.data_path = f'pose.bones["{renames[m.group(1)]}"].{m.group(2)}'

# 2. action renames ------------------------------------------------------------
for a in bpy.data.actions:
    a.name = a.name.replace("Mixamo - ", "")

LOWER = {"rootJoint", "Spinelower"}
for side in ("L", "R"):
    LOWER |= {f"LegThigh.{side}", f"LegShin.{side}", f"Footctrl.{side}", f"Foot.{side}",
              f"FootToe.{side}", f"Poleleg.{side}"}
ROOT_CHILD_UPPER = ["HandWrist.L", "HandWrist.R", "PoleArm.L", "PoleArm.R", "SMGBone", "SHOTGUNbone"]


def flip(name):
    if ".L" in name:
        return name.replace(".L", ".R")
    if ".R" in name:
        return name.replace(".R", ".L")
    return name


def assign(action):
    arm.animation_data.action = action
    if action.slots:
        arm.animation_data.action_slot = action.slots[0]


def frames(action):
    f0, f1 = (int(round(x)) for x in action.frame_range)
    return range(f0, f1 + 1)


def parse(fc):
    m = re.match(r'pose\.bones\["(.+?)"\]\.(.+)', fc.data_path)
    return m.group(1), m.group(2)


def pose_at(action, f):
    """Evaluate the action at frame f into the pose.

    frame_set() skips evaluation when the frame number is unchanged, even if
    the action changed, and hands back the previous pose; stepping to a frame
    no clip uses first forces a real evaluation."""
    assign(action)
    bpy.context.scene.frame_set(-10000)
    bpy.context.scene.frame_set(f)


def write_keys(dst, keys):
    for (bone, prop, idx), pts in keys.items():
        fc = dst.fcurve_ensure_for_datablock(arm, f'pose.bones["{bone}"].{prop}', index=idx, group_name=bone)
        fc.keyframe_points.add(len(pts))
        for i, (t, v) in enumerate(pts):
            fc.keyframe_points[i].co = (t, v)
            fc.keyframe_points[i].interpolation = "LINEAR"
        fc.update()


def pose_vector(action, f):
    """Local channels of every bone except the root's horizontal travel."""
    v = []
    for fc in channelbag(action).fcurves:
        bone, prop = parse(fc)
        if bone == "rootJoint" and prop == "location" and fc.array_index in (0, 2):
            continue
        v.append(fc.evaluate(f))
    return v


def make_loop(src_name, dst_name, min_len, max_len, search_from):
    """Cut the cleanest loop out of a clip that starts from a standstill."""
    src = bpy.data.actions[src_name]
    f0, f1 = (int(round(x)) for x in src.frame_range)
    vecs = {f: pose_vector(src, f) for f in range(f0, f1 + 1)}
    best = None
    for a in range(max(f0, search_from), f1 + 1):
        for b in range(a + min_len, min(a + max_len, f1) + 1):
            d = sum((x - y) ** 2 for x, y in zip(vecs[a], vecs[b])) ** 0.5
            if best is None or d < best[0]:
                best = (d, a, b)
    d, a, b = best
    keys = {}
    for fc in channelbag(src).fcurves:
        bone, prop = parse(fc)
        keys[(bone, prop, fc.array_index)] = [(f - a + 1, fc.evaluate(f)) for f in range(a, b + 1)]
    dst = bpy.data.actions.new(dst_name)
    dst.use_fake_user = True
    assign(dst)
    write_keys(dst, keys)
    assign(dst)
    report.setdefault("loops", {})[dst_name] = {"from": src_name, "frames": [a, b], "pose_error": round(d, 3)}
    return dst


def close_cycle(action):
    """Match the pose and velocity on either side of the wrap."""
    start, end = action.frame_range
    segments = round((end - start) * EXPORT_FPS / FPS)
    times = [start + (end - start) * i / segments for i in range(segments + 1)]
    window = round(EXPORT_FPS / 12)  # two source frames at each end
    for curve in channelbag(action).fcurves:
        values = [curve.evaluate(t) for t in times]
        seam = (values[0] + values[-1]) * 0.5
        slope = ((values[1] - values[0]) + (values[-1] - values[-2])) * 0.5
        values[0] = values[-1] = seam
        # Retain the interior of the gait while matching the first and last
        # intervals exactly. Ease the correction out over the nearby samples.
        start_delta = seam + slope - values[1]
        end_delta = seam - slope - values[-2]
        for i in range(1, window + 1):
            t = (i - 1) / window
            blend = (1 - t) ** 2 * (1 + 2 * t)
            values[i] += blend * start_delta
            values[-1 - i] += blend * end_delta
        curve.keyframe_points.clear()
        curve.keyframe_points.add(len(times))
        curve.keyframe_points.foreach_set("co", [v for key in zip(times, values) for v in key])
        for key in curve.keyframe_points:
            key.interpolation = "LINEAR"
        curve.update()


def hybrid_clip(dst_name, lower_name, upper_name, mirror_lower):
    """Legs and hips from one clip, upper body (torso, arms, hands, SMG) from another.

    The upper body keeps its armature-space pose from `upper_name`, re-seated
    on the chest of the new clip, so the hands stay on the gun and the gun
    points where it pointed in the upper clip. The rig parents the hands, arm
    poles and weapons to the root rather than to the arms, so a Unity
    avatar-mask layer cannot do this split; it has to be baked.

    With mirror_lower, the legs are mirrored left/right. The rest pose is
    symmetric with Blender's roll convention (verified), so a local mirror is
    loc.x -> -x, quat (w,x,y,z) -> (w,x,-y,-z) on the paired bone."""
    lower = bpy.data.actions[lower_name]
    upper = bpy.data.actions[upper_name]
    l0, l1 = (int(round(x)) for x in lower.frame_range)
    u0, u1 = (int(round(x)) for x in upper.frame_range)

    def uf(f):
        return f if upper is lower else u0 + (f - l0) % max(1, u1 - u0)

    keys = {}
    for fc in channelbag(lower).fcurves:
        bone, prop = parse(fc)
        if bone not in LOWER:
            continue
        tb, sign = bone, 1.0
        if mirror_lower:
            tb = flip(bone)
            if (prop == "location" and fc.array_index == 0) or \
                    (prop == "rotation_quaternion" and fc.array_index in (2, 3)):
                sign = -1.0
        keys[(tb, prop, fc.array_index)] = [(f, fc.evaluate(f) * sign) for f in range(l0, l1 + 1)]
    for fc in channelbag(upper).fcurves:
        bone, prop = parse(fc)
        if bone in LOWER:
            continue
        keys[(bone, prop, fc.array_index)] = [(f, fc.evaluate(uf(f))) for f in range(l0, l1 + 1)]
    dst = bpy.data.actions.new(dst_name)
    dst.use_fake_user = True
    assign(dst)
    write_keys(dst, keys)
    assign(dst)

    pb_by = arm.pose.bones
    worst = 0.0
    for f in range(l0, l1 + 1):
        pose_at(upper, uf(f))
        src_chest = pb_by["Spineupper"].head.copy()
        src_spine = pb_by["Spineupper"].matrix.to_quaternion()
        src_mats = {n: pb_by[n].matrix.copy() for n in ROOT_CHILD_UPPER}
        src_rel = {n: src_mats[n].translation - src_chest for n in ROOT_CHILD_UPPER}
        pose_at(dst, f)
        sp = pb_by["Spineupper"]
        sp.matrix = mathutils.Matrix.Translation(sp.matrix.translation) @ src_spine.to_matrix().to_4x4()
        bpy.context.view_layer.update()
        delta = pb_by["Spineupper"].head - src_chest
        for n in ROOT_CHILD_UPPER:
            pb_by[n].matrix = mathutils.Matrix.Translation(delta) @ src_mats[n]
        bpy.context.view_layer.update()
        for n in ["Spineupper"] + ROOT_CHILD_UPPER:
            pb_by[n].keyframe_insert("location", frame=f, group=n)
            pb_by[n].keyframe_insert("rotation_quaternion", frame=f, group=n)
        # check: hands sit where the upper clip had them, relative to the chest
        chest = pb_by["Spineupper"].head
        for n in ROOT_CHILD_UPPER:
            worst = max(worst, (pb_by[n].matrix.translation - chest - src_rel[n]).length)
    report.setdefault("hybrids", {})[dst_name] = {
        "lower": lower_name, "upper": upper_name, "mirrored_legs": mirror_lower, "hand_drift_max": round(worst, 4)}
    return dst


report = {"renamed_bones": len(renames)}
# The full rifle run returns both feet to their starting phase.
make_loop("Shoot Rifle", "Loop Run Forward", 28, 28, 1)
make_loop("Backwards Rifle Walk", "Loop Walk Back", 14, 38, 5)
make_loop("Strafing", "Loop Strafe Right", 10, 30, 26)
make_loop("Walk Forward Left", "Loop Walk Forward Left", 14, 30, 1)
# The rig has no left strafe or forward-right walk: mirror the legs, keep the
# right-handed upper body.
hybrid_clip("Loop Strafe Left", "Loop Strafe Right", "Loop Strafe Right", mirror_lower=True)
# Idle, run and strafes already point the SMG forward; walk-back holds it low to
# the left and the diagonals at port arms. Give those the aiming upper body.
hybrid_clip("Aim Walk Back", "Loop Walk Back", "Rifle Aiming Idle", mirror_lower=False)
hybrid_clip("Aim Walk Forward Left", "Loop Walk Forward Left", "Rifle Aiming Idle", mirror_lower=False)
hybrid_clip("Aim Walk Forward Right", "Loop Walk Forward Left", "Rifle Aiming Idle", mirror_lower=True)

# 4/5. measure travel, then make clips in-place ----------------------------------
speeds = {}
for a in bpy.data.actions:
    cb = channelbag(a)
    f0, f1 = a.frame_range
    dur = (f1 - f0) / FPS
    travel = {}
    for idx in (0, 2):  # rootJoint local X = world X, local Z = character forward
        fc = cb.fcurves.find('pose.bones["rootJoint"].location', index=idx)
        if fc is None:
            continue
        v0, v1 = fc.evaluate(f0), fc.evaluate(f1)
        travel[idx] = v1 - v0
        for k in fc.keyframe_points:
            t = (k.co[0] - f0) / (f1 - f0) if f1 > f0 else 0
            off = (v0 - REST) + (v1 - v0) * t  # also re-centre clips cut mid-travel
            k.co[1] -= off
            k.handle_left[1] -= off
            k.handle_right[1] -= off
        fc.update()
    # rootJoint local +Z = world -Y = character forward; local +X = world +X = character left
    fwd = travel.get(2, 0.0) * UNITY_SCALE / dur if dur else 0
    left = travel.get(0, 0.0) * UNITY_SCALE / dur if dur else 0
    speeds[a.name] = {"frames": int(f1 - f0 + 1), "seconds": round(dur, 3),
                      "forward_mps": round(fwd, 2), "left_mps": round(left, 2)}
report["clips"] = speeds

for name in ("Walking", "Walking Backwards", "Crouch Walking", "Loop Run Forward", "Sprint Forward"):
    close_cycle(bpy.data.actions[name])

# Blender's FBX sampler uses the scene frame rate for every take. Retiming
# the keys before doubling the rate keeps the original durations.
time_scale = EXPORT_FPS / FPS
for action in bpy.data.actions:
    start, end = action.frame_range
    if action.use_frame_range:
        action.frame_end = end * time_scale
        action.frame_start = start * time_scale
    for curve in channelbag(action).fcurves:
        for key in curve.keyframe_points:
            key.co.x *= time_scale
            key.handle_left.x *= time_scale
            key.handle_right.x *= time_scale
        curve.update()
    speeds[action.name]["frames"] = round((end - start) * time_scale) + 1
bpy.context.scene.render.fps = EXPORT_FPS
report["export_fps"] = EXPORT_FPS

# 6. export ----------------------------------------------------------------------
for img in bpy.data.images:
    if img.packed_file:
        path = os.path.join(OUT, f"Soldier_{img.name}.png")
        img.filepath_raw = path
        img.file_format = "PNG"
        img.save()
report["materials"] = {}
for o in arm.children:
    if o.type == "MESH":
        for m in o.data.materials:
            if m and m.use_nodes:
                images = [n for n in m.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
                report["materials"][m.name] = [n.image.name for n in images]
                if not images:
                    continue
                # The original material is unlit. FBX's exporter reads image
                # links on Principled nodes, so expose its atlas for export.
                nodes = m.node_tree.nodes
                shader = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
                if shader is None:
                    shader = nodes.new("ShaderNodeBsdfPrincipled")
                output = next(n for n in nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output)
                m.node_tree.links.new(images[0].outputs["Color"], shader.inputs["Base Color"])
                m.node_tree.links.new(shader.outputs["BSDF"], output.inputs["Surface"])

assign(bpy.data.actions["Rifle Aiming Idle"])
bpy.context.scene.frame_set(round(time_scale))
for o in bpy.context.view_layer.objects:
    o.select_set(False)
arm.select_set(True)
for o in arm.children:
    if o.type == "MESH":
        o.select_set(True)
bpy.context.view_layer.objects.active = arm
fbx = os.path.join(OUT, "Soldier.fbx")
bpy.ops.export_scene.fbx(
    filepath=fbx, use_selection=True, object_types={"ARMATURE", "MESH"},
    add_leaf_bones=False, bake_anim=True, bake_anim_use_all_bones=True,
    bake_anim_use_nla_strips=False, bake_anim_use_all_actions=True,
    bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
    path_mode="COPY", embed_textures=True, mesh_smooth_type="FACE",
    apply_scale_options="FBX_SCALE_ALL",  # Unity then reads 1 unit = 1 Blender unit
)
report["fbx"] = fbx
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "Soldier_processed.blend"), copy=True)
print("REPORT:" + json.dumps(report))
