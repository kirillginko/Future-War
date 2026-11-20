"""Convert the MH-6 Little Bird GLB into a Unity-ready FBX.

Blender -b --python Tools/Blender/import_helicopter.py -- <helicopters_mh-6_little_bird.glb> <output_folder>

Keeps the main rotor ("helice 1_3") and tail rotor ("helice 2_2") as their own
nodes, pivoted on their hubs, so HeliRotorController can spin them. The model
is already in metres (8.7 m nose to tail). Writes LittleBird.fbx and the
packed texture as LittleBird_Texture.png.
"""
import bpy
import sys
from pathlib import Path

glb, out = map(Path, sys.argv[sys.argv.index("--") + 1:])
out.mkdir(parents=True, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(glb))

for img in bpy.data.images:
    if img.packed_file or img.has_data:
        img.filepath_raw = str(out / "LittleBird_Texture.png")
        img.file_format = "PNG"
        img.save()

# Friendlier names for the nodes Unity code looks up.
renames = {"helice 1_3": "MainRotor", "helice 2_2": "TailRotor", "Sketchfab_model": "LittleBird"}
for old, new in renames.items():
    if old in bpy.data.objects:
        bpy.data.objects[old].name = new

bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.fbx(
    filepath=str(out / "LittleBird.fbx"),
    use_selection=True,
    object_types={"EMPTY", "MESH"},
    apply_scale_options="FBX_SCALE_ALL",
    path_mode="COPY",
    embed_textures=False,
    mesh_smooth_type="FACE",
    add_leaf_bones=False,
    bake_anim=False,
)
print("WROTE", out / "LittleBird.fbx")
