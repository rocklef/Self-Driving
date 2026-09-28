# Exports every named object under a parent node of a glTF pack as its own GLB (origin at the
# bottom centre of its footprint, world scale), with a 4-view thumbnail strip (-Y, +X, +Y, -X)
# to see which side is the front.
#
# blender -b --python tools/blender_export_nodes.py -- <in.gltf> <parentNodeName> <out_dir> [name,name,...]
import bpy, sys, os
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
src, parent_name, out_dir = argv[0], argv[1], argv[2]
only = set(argv[3].split(",")) if len(argv) > 3 else None
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
scene = bpy.context.scene
bpy.context.view_layer.update()
root = bpy.data.objects[parent_name]
items = [c for c in root.children if only is None or c.name in only]

scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "FLAT"
scene.display.shading.color_type = "TEXTURE"
scene.render.resolution_x = scene.render.resolution_y = 200
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam

def tree(o):
    yield o
    for c in o.children: yield from tree(c)

for i, node in enumerate(items):
    objs = list(tree(node))
    pts = [o.matrix_world @ Vector(c) for o in objs if o.type == "MESH" for c in o.bound_box]
    if not pts: continue
    x0, x1 = min(p.x for p in pts), max(p.x for p in pts)
    y0, y1 = min(p.y for p in pts), max(p.y for p in pts)
    z0, z1 = min(p.z for p in pts), max(p.z for p in pts)
    pivot = bpy.data.objects.new(node.name, None)
    scene.collection.objects.link(pivot)
    pivot.location = ((x0 + x1) / 2, (y0 + y1) / 2, z0)
    bpy.context.view_layer.update()
    mw = node.matrix_world.copy()
    node.parent = pivot
    node.matrix_parent_inverse = pivot.matrix_world.inverted()
    node.matrix_world = mw
    pivot.location = (0, 0, 0)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    pivot.select_set(True)
    for o in objs: o.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(out_dir, node.name + ".glb"), use_selection=True, export_format="GLB")
    w, d, h = x1 - x0, y1 - y0, z1 - z0
    print(f"[NODE] {node.name}: {w:.2f} x {d:.2f} x {h:.2f} m")
    shown = set(objs) | {pivot, cam}
    for o in scene.objects: o.hide_render = o not in shown
    r = max(w, d, h)
    for k, v in enumerate([(0, -1), (1, 0), (0, 1), (-1, 0)]):
        cam.location = (v[0] * 2.4 * r, v[1] * 2.4 * r, h * 0.6)
        cam.rotation_euler = (Vector((0, 0, h * 0.5)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(out_dir, f"{node.name}_v{k}.png")
        bpy.ops.render.render(write_still=True)
    pivot.location = (5000 + i * 100, 0, 0)
