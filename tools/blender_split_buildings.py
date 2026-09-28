# Splits a building pack (one glTF scene with many buildings) into one GLB per building.
# Meshes whose ground footprints touch (within GAP metres) are grouped; groups too small to be a
# building (trees, props) are skipped. Each GLB has its origin at the bottom centre of the footprint.
# A thumbnail PNG is rendered per building for checking.
#
# blender -b --python tools/blender_split_buildings.py -- <in.gltf> <out_dir> <prefix> [gap] [minHeight]
import bpy, sys, os, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
src, out_dir, prefix = argv[0], argv[1], argv[2]
GAP = float(argv[3]) if len(argv) > 3 else 0.3
MIN_H = float(argv[4]) if len(argv) > 4 else 5.0
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
bpy.context.view_layer.update()

def bbox(o):
    pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
    return (min(p.x for p in pts), max(p.x for p in pts), min(p.y for p in pts), max(p.y for p in pts),
            min(p.z for p in pts), max(p.z for p in pts))

boxes = {o.name: bbox(o) for o in meshes}
parent = {o.name: o.name for o in meshes}
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a

names = list(boxes)
for i, a in enumerate(names):
    A = boxes[a]
    for b in names[i + 1:]:
        B = boxes[b]
        if A[0] - GAP <= B[1] and B[0] - GAP <= A[1] and A[2] - GAP <= B[3] and B[2] - GAP <= A[3]:
            parent[find(a)] = find(b)

groups = {}
for n in names: groups.setdefault(find(n), []).append(n)

def group_box(ns):
    bs = [boxes[n] for n in ns]
    return (min(b[0] for b in bs), max(b[1] for b in bs), min(b[2] for b in bs), max(b[3] for b in bs),
            min(b[4] for b in bs), max(b[5] for b in bs))

# Workbench thumbnails
scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "TEXTURE"
scene.render.resolution_x = scene.render.resolution_y = 360
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam

kept = 0
for gi, ns in enumerate(sorted(groups.values(), key=lambda g: group_box(g)[0])):
    x0, x1, y0, y1, z0, z1 = group_box(ns)
    w, d, h = x1 - x0, y1 - y0, z1 - z0
    ok = h >= MIN_H and max(w, d) >= 4.0
    print(f"[SPLIT] group {gi}: {len(ns)} meshes  w={w:.1f} d={d:.1f} h={h:.1f}  {'KEEP' if ok else 'skip'}")
    if not ok: continue
    name = f"{prefix}_{kept:02d}"
    kept += 1
    root = bpy.data.objects.new(name, None)
    scene.collection.objects.link(root)
    root.location = ((x0 + x1) / 2, (y0 + y1) / 2, z0)
    bpy.context.view_layer.update()
    for n in ns:
        o = bpy.data.objects[n]
        mw = o.matrix_world.copy()
        o.parent = root
        o.matrix_parent_inverse = root.matrix_world.inverted()
        o.matrix_world = mw
    root.location = (0, 0, 0)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for n in ns: bpy.data.objects[n].select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(out_dir, name + ".glb"), use_selection=True, export_format="GLB")
    # thumbnail: hide everything else, look from the front-left (-Y is the glTF front after import)
    for o in scene.objects: o.hide_render = not (o == root or o.parent == root or o == cam)
    r = max(w, d, h)
    cam.location = (-0.6 * r, -1.6 * r, 0.9 * h + 0.3 * r)
    cam.rotation_euler = (Vector((0, 0, h / 2)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = os.path.join(out_dir, name + ".png")
    bpy.ops.render.render(write_still=True)
    # move it far away so later groups do not overlap it in anything
    root.location = (10000 + gi * 200, 0, 0)
    print(f"[SPLIT] wrote {name}: {w:.1f} x {d:.1f} x {h:.1f} m")
print(f"[SPLIT] {kept} buildings")
