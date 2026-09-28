# Blender (headless): cut the four wheels out of a single-mesh car GLB so they can spin.
# blender -b --python tools/blender_split_wheels.py -- <in.glb> <out.glb> <preview.png>
# Car length runs along X, width along Y, up is Z (Blender axes after glTF import).
import bpy, sys, bmesh, math
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
src, dst, preview = args[0], args[1], args[2]
TYRE_FRACTION = float(args[3]) if len(args) > 3 else 0.76

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
body = next(o for o in bpy.context.scene.objects if o.type == "MESH")
bpy.context.view_layer.objects.active = body
body.select_set(True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

me = body.data
co = [v.co.copy() for v in me.vertices]
lo = Vector((min(p.x for p in co), min(p.y for p in co), min(p.z for p in co)))
hi = Vector((max(p.x for p in co), max(p.y for p in co), max(p.z for p in co)))
height = hi.z - lo.z
halfw = (hi.y - lo.y) / 2
cy = (hi.y + lo.y) / 2
print(f"BOUNDS lo={tuple(round(v,3) for v in lo)} hi={tuple(round(v,3) for v in hi)}")

# Tyre candidates: low, and on the outer sides of the car.
low_z = lo.z + height * 0.36
side_y = halfw * 0.62
wheels = {}
for sx in (1, -1):
    for sy in (1, -1):
        pts = [p for p in co if p.z < low_z and (p.y - cy) * sy > side_y and p.x * sx > 0.12]
        if not pts:
            continue
        xs = sorted(p.x for p in pts)
        z0 = min(p.z for p in pts)
        # The arch (fender) is wider than the tyre; the tyre is ~TYRE_FRACTION of it.
        x0, x1 = xs[int(len(xs) * 0.03)], xs[int(len(xs) * 0.97) - 1]
        r = max((x1 - x0) / 2, 0.02) * TYRE_FRACTION
        # Centre x from the contact patch (lowest points).
        contact = [p.x for p in pts if p.z < z0 + height * 0.03]
        cxw = sum(contact) / len(contact)
        centre = Vector((cxw, 0, z0 + r))
        inner = min(abs(p.y - cy) for p in pts)
        wheels[(sx, sy)] = (centre, r, inner)
        print(f"WHEEL sx={sx} sy={sy} centre=({centre.x:.3f},{centre.z:.3f}) r={r:.3f} inner_y={inner:.3f} n={len(pts)}")

names = {(1, 1): "Wheel_FL", (1, -1): "Wheel_FR", (-1, 1): "Wheel_RL", (-1, -1): "Wheel_RR"}
wheel_objs = []
for key, (centre, r, inner) in wheels.items():
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.mode_set(mode="EDIT")
    bm = bmesh.from_edit_mesh(body.data)
    for f in bm.faces:
        f.select = False
    sx, sy = key
    for f in bm.faces:
        c = f.calc_center_median()
        d = math.hypot(c.x - centre.x, c.z - centre.z)
        if d <= r and (c.y - cy) * sy > inner - 0.015:
            f.select = True
    bmesh.update_edit_mesh(body.data)
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    w = [o for o in bpy.context.selected_objects if o != body][0]
    w.name = names[key]
    # pivot on the axle: put the origin at the wheel centre (x, mean y, z)
    ys = [v.co.y for v in w.data.vertices]
    pivot = Vector((centre.x, sum(ys) / max(len(ys), 1), centre.z))
    bpy.context.scene.cursor.location = pivot
    bpy.ops.object.select_all(action="DESELECT")
    w.select_set(True)
    bpy.context.view_layer.objects.active = w
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
    wheel_objs.append(w)
    print(f"SPLIT {w.name}: {len(w.data.polygons)} faces")

body.name = "Body"
bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.gltf(filepath=dst, export_format="GLB", use_selection=True)

# Preview: wheels tinted red so the cut is easy to judge.
red = bpy.data.materials.new("check")
red.diffuse_color = (1, 0, 0, 1)
red.use_nodes = True
red.node_tree.nodes["Principled BSDF"].inputs[0].default_value = (1, 0.05, 0.05, 1)
for w in wheel_objs:
    w.data.materials.clear()
    w.data.materials.append(red)
scene = bpy.context.scene
scene.render.resolution_x, scene.render.resolution_y = 1000, 560
scene.render.filepath = preview
world = bpy.data.worlds.new("W"); scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[1].default_value = 1.0
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN")); sun.data.energy = 3
sun.rotation_euler = (math.radians(50), 0, math.radians(30)); scene.collection.objects.link(sun)
cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam")); scene.collection.objects.link(cam); scene.camera = cam
cam.location = Vector((0.9, -1.4, 0.35))
cam.rotation_euler = (Vector((0, 0, lo.z + height * 0.3)) - cam.location).to_track_quat("-Z", "Y").to_euler()
bpy.ops.render.render(write_still=True)
