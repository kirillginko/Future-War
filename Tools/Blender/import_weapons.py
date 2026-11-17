"""Convert the three downloaded GLBs to static, metre-scale Unity FBXs.

Blender -b --python Tools/Blender/import_weapons.py -- <source_folder> <output_folder>
Axes and grip positions refer to the evaluated GLB geometry, before conversion.
The FAMAS's source skeleton is evaluated once and removed from the export.
"""
import bpy
import json
import math
import sys
from pathlib import Path
from mathutils import Vector

source, output = map(Path, sys.argv[sys.argv.index("--") + 1:])
output.mkdir(parents=True, exist_ok=True)
# All three evaluated models point +X with +Z up. Their stock is behind the grip.
weapons = {
    "pp-19_bizon": dict(length=.66, grip=(.30, 0, -.95), muzzle=(8.62, 0, .87),
                        sight=(1.42, .055, 2.70), support=(4.0, 0, -.65), stock=(-5.0, 0, 0)),
    "pp-19-01": dict(length=.69, grip=(.20, 0, -1.10), muzzle=(8.77, 0, .87),
                     sight=(1.75, .065, 2.52), support=(5.95, 0, -1.0), stock=(-5.3, 0, .45)),
    "low-poly_famas_f1": dict(length=.757, grip=(-35, 0, -85), muzzle=(434, 0, 12),
                             sight=(-93, 0, 126), support=(130, 0, -52), stock=(-428, 0, 0)),
}
for name, settings in weapons.items():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source / f"{name}.glb"))
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    grip = Vector(settings["grip"])
    # Use each weapon's compact proportions; the original includes a long suppressor.
    # Evaluate the source first: the FAMAS skeleton changes its mesh dimensions.
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH" and o.name != "Icosphere"]
    forward = [
        (o.evaluated_get(depsgraph).matrix_world @ v.co).x
        for o in meshes for v in o.evaluated_get(depsgraph).data.vertices
    ]
    scale = settings["length"] / (max(forward) - min(forward))

    def convert(p):
        p = (Vector(p) - grip) * scale
        return Vector((p.y, -p.x, p.z))

    # Capture evaluated meshes before removing any source hierarchy or constraints.
    captured = []
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH" or obj.name == "Icosphere":
            continue
        evaluated = obj.evaluated_get(depsgraph)
        mesh = bpy.data.meshes.new_from_object(evaluated, depsgraph=depsgraph)
        for vertex in mesh.vertices:
            point = evaluated.matrix_world @ vertex.co
            vertex.co = convert(point)
        captured.append((obj.name, mesh))
    for obj in list(bpy.context.scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    root = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(root)
    for mesh_name, mesh in captured:
        obj = bpy.data.objects.new(mesh_name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = root
    for marker in ("muzzle", "sight", "support", "grip", "stock"):
        obj = bpy.data.objects.new(marker.capitalize(), None)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = root
        obj.location = convert(settings[marker])
        obj.rotation_euler.x = math.pi / 2

    # Record original material colours and packed textures for the URP materials.
    materials = {}
    for obj in root.children:
        if obj.type != "MESH":
            continue
        for material in obj.data.materials:
            if material is None or material.name in materials:
                continue
            shader = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
            images = [n.image for n in material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
            texture = None
            if images:
                texture = f"{name}_{material.name}.png"
                images[0].filepath_raw = str(output / texture)
                images[0].file_format = "PNG"
                images[0].save()
            materials[material.name] = dict(texture=texture, color=list(shader.inputs["Base Color"].default_value))
    for obj in bpy.context.scene.objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=str(output / f"{name}.fbx"), use_selection=True,
        object_types={"MESH", "EMPTY"}, bake_anim=False, add_leaf_bones=False,
        path_mode="COPY", embed_textures=True, apply_scale_options="FBX_SCALE_ALL")
    (output / f"{name}.json").write_text(json.dumps({"materials": [dict(name=k, **v) for k, v in materials.items()]}, indent=2))
    print("EXPORTED", name, settings)
