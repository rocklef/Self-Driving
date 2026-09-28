# Blender (headless): report the loose parts of a GLB, largest first.
# blender -b --python tools/blender_parts.py -- <file.glb>
import bpy, sys, bmesh
from mathutils import Vector

path = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=path)
for o in bpy.context.scene.objects:
    if o.type != "MESH":
        continue
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.verts.ensure_lookup_table()
    seen, parts = set(), []
    for v in bm.verts:
        if v.index in seen:
            continue
        stack, island = [v], []
        seen.add(v.index)
        while stack:
            cur = stack.pop()
            island.append(cur)
            for e in cur.link_edges:
                w = e.other_vert(cur)
                if w.index not in seen:
                    seen.add(w.index)
                    stack.append(w)
        pts = [o.matrix_world @ x.co for x in island]
        lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
        hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        parts.append((len(island), lo, hi))
    parts.sort(key=lambda p: -p[0])
    print(f"PARTS {o.name}: {len(parts)} islands, {len(bm.verts)} verts")
    for n, lo, hi in parts[:15]:
        c = (lo + hi) / 2
        print(f"PART verts={n:6d} center=({c.x:.3f},{c.y:.3f},{c.z:.3f}) size=({hi.x-lo.x:.3f},{hi.y-lo.y:.3f},{hi.z-lo.z:.3f})")
