using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Vehicles (Sketchfab packs + the robotaxi), street furniture, trees, obstacles, sky and
// photo-scanned surfaces (Poly Haven, CC0). Called from SceneBuilder.
public static class CityDressing
{
    const string PH = "Assets/PolyHaven";
    const string VehicleDir = "Assets/Vehicles";

    // Which way each source's models point along their long axis (checked with InspectVehicles renders).
    public const bool FlipPassengerPack = true, FlipCivilPack = true, FlipBoxTruck = true, FlipRobotaxi = true;

    // ------------------------------------------------------------------ vehicles

    public class VehicleParts
    {
        public string name;
        public Transform frame;                                  // the node the model was authored in
        public List<Transform> body = new List<Transform>();
        public List<Transform> wheels = new List<Transform>();
    }

    static Bounds RendererBounds(IEnumerable<Transform> parts)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var t in parts)
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
        return b;
    }

    // Exact bounds of every mesh under `parts`, measured in `frame`'s own axes. Showroom packs
    // place models at arbitrary angles, so a world axis-aligned box would be the wrong shape.
    static Bounds LocalBounds(Transform frame, IEnumerable<Transform> parts)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var t in parts)
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                var m = frame.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
        return b;
    }

    // Index (0 = x, 1 = y, 2 = z) of the frame axis that points most nearly up in the world.
    static int VerticalAxis(Transform f)
    {
        Vector3[] axes = { f.right, f.up, f.forward };
        int v = 0;
        for (int i = 1; i < 3; i++)
            if (Mathf.Abs(Vector3.Dot(axes[i], Vector3.up)) > Mathf.Abs(Vector3.Dot(axes[v], Vector3.up))) v = i;
        return v;
    }

    // World heading of a vehicle: its longest horizontal axis in its own frame.
    static Vector3 Heading(VehicleParts parts, bool flip)
    {
        var f = parts.frame;
        Vector3[] axes = { f.right, f.up, f.forward };
        int vertical = VerticalAxis(f);
        var size = LocalBounds(f, parts.body).size;
        float[] ext = { size.x, size.y, size.z };
        int longAxis = -1;
        for (int i = 0; i < 3; i++)
            if (i != vertical && (longAxis < 0 || ext[i] > ext[longAxis])) longAxis = i;
        var h = Vector3.ProjectOnPlane(axes[longAxis], Vector3.up).normalized;
        return flip ? -h : h;
    }

    // Root (unit scale, forward = +Z, origin on the ground) > "Model" (scaled) > body parts + wheel pivots.
    public static GameObject MakeVehicle(VehicleParts parts, float targetLength, bool flip, out Transform[] pivots, out bool[] steers, out float wheelRadius)
    {
        var heading = Heading(parts, flip);
        var b = RendererBounds(parts.body.Concat(parts.wheels));
        var model = new GameObject("Model").transform;
        model.SetPositionAndRotation(new Vector3(b.center.x, b.min.y, b.center.z), Quaternion.LookRotation(heading));
        foreach (var t in parts.body) t.SetParent(model, true);

        var pivotList = new List<Transform>();
        foreach (var w in parts.wheels)
        {
            var pivot = new GameObject("WheelPivot").transform;
            pivot.SetPositionAndRotation(RendererBounds(new[] { w }).center, model.rotation);   // X = axle
            pivot.SetParent(model, true);
            w.SetParent(pivot, true);
            pivotList.Add(pivot);
        }

        // Re-root: zeroing the model's rotation under a fresh root turns the vehicle to face +Z.
        var root = new GameObject(parts.name);
        model.SetParent(root.transform, false);
        model.localPosition = Vector3.zero;
        model.localRotation = Quaternion.identity;

        var lb = RendererBounds(new[] { model });
        if (targetLength > 0f && lb.size.z > 0.01f) model.localScale *= targetLength / lb.size.z;
        lb = RendererBounds(new[] { model });
        model.position += new Vector3(-lb.center.x, -lb.min.y, -lb.center.z);
        lb = RendererBounds(new[] { model });

        var col = root.AddComponent<BoxCollider>();
        col.center = lb.center;
        col.size = lb.size;
        if (lb.size.x > 2.9f || lb.size.x > lb.size.z * 0.7f)
            Debug.LogWarning($"CityDressing: {parts.name} came out {lb.size.x:0.00} m wide x {lb.size.z:0.00} m long; check its heading/wheels.");

        pivots = pivotList.ToArray();
        steers = pivots.Select(p => p.localPosition.z > 0f).ToArray();
        wheelRadius = pivots.Length > 0 ? RendererBounds(new[] { pivots[0] }).size.y * 0.5f : 0.33f;
        return root;
    }

    // Adds driving, sensing, wheel animation and viz tracking to a vehicle root.
    public static void MakeDrivable(GameObject car, RoadPath road, float s, int lane, float cruise, Transform[] pivots, bool[] steers, float radius, VizTracked.Kind kind)
    {
        car.transform.position = road.Point(s, lane);
        car.transform.rotation = Quaternion.LookRotation(road.Tangent(s));
        car.AddComponent<Rigidbody>().isKinematic = true;
        var cc = car.AddComponent<CarController>();
        cc.wheelBase = car.GetComponent<BoxCollider>().size.z * 0.6f;
        var sensors = car.AddComponent<SensorArray>();
        sensors.showRays = kind == VizTracked.Kind.Ego;
        var ap = car.AddComponent<AutoPilot>();
        ap.road = road;
        ap.cruiseSpeed = cruise;
        ap.startLane = lane;
        var spin = car.AddComponent<WheelSpinner>();
        spin.pivots = pivots;
        spin.steers = steers;
        spin.radius = radius;
        car.AddComponent<VizTracked>().kind = kind;
    }

    static GameObject Load(string path)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (!go) Debug.LogWarning("CityDressing: missing " + path);
        return go;
    }

    static GameObject Unpacked(GameObject prefab)
    {
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        return inst;
    }

    // Splits the showroom car pack into vehicles: every "...Body" node plus the four wheels that
    // sit inside its footprint, measured in the body's own (rotated) frame.
    public static List<VehicleParts> SplitPassengerPack(GameObject pack)
    {
        var all = pack.GetComponentsInChildren<Transform>();
        var bodies = all.Where(t => t.name.EndsWith("Body") || t.name.EndsWith("body")).ToList();
        var wheels = all.Where(t => t.name.StartsWith("Wheel_") && t.GetComponentInChildren<Renderer>() && t.parent && !t.parent.name.StartsWith("Wheel_")).ToList();
        var list = new List<VehicleParts>();
        foreach (var body in bodies)
        {
            var lb = LocalBounds(body, new[] { body });
            int v = VerticalAxis(body);
            bool Inside(Transform w)
            {
                var c = body.InverseTransformPoint(RendererBounds(new[] { w }).center);
                for (int a = 0; a < 3; a++)
                {
                    if (a == v) continue;
                    float margin = lb.size[a] * 0.08f;
                    if (c[a] < lb.min[a] - margin || c[a] > lb.max[a] + margin) return false;
                }
                return true;
            }
            var mine = wheels.Where(Inside)
                .OrderBy(w => (RendererBounds(new[] { w }).center - body.TransformPoint(lb.center)).sqrMagnitude)
                .Take(4).ToList();
            foreach (var w in mine) wheels.Remove(w);
            list.Add(new VehicleParts { name = body.name.Replace(" Body", "").Replace(" body", ""), frame = body, body = { body }, wheels = mine });
        }
        return list;
    }

    // Traffic plan: every vehicle follows one closed route through the street grid. All streets are
    // one-way, so on any street everything flows the same way. Spawn points are given per street.
    public static GameObject BuildTraffic(CityNetwork net, GameObject robotaxiModel, Transform parent)
    {
        void Drive(GameObject car, string route, string street, float s, float cruise, Transform[] p, bool[] st, float r, VizTracked.Kind kind)
        {
            var path = net.routes[route];
            MakeDrivable(car, path, path.Project(net.streets[street].Center(s)), 0, cruise, p, st, r, kind);
            car.transform.SetParent(parent);
        }

        // ---- the robotaxi (wheels cut out in Blender, see tools/blender_split_wheels.py): the grand tour
        var taxiInst = Unpacked(robotaxiModel);
        var taxiParts = new VehicleParts { name = "Robotaxi" };
        foreach (var t in taxiInst.GetComponentsInChildren<Transform>())
        {
            if (!t.GetComponent<Renderer>()) continue;
            (t.name.StartsWith("Wheel_") ? taxiParts.wheels : taxiParts.body).Add(t);
        }
        taxiParts.frame = taxiParts.body[0];
        var robotaxi = MakeVehicle(taxiParts, 3.5f, FlipRobotaxi, out var tp, out var ts, out var tr);
        Object.DestroyImmediate(taxiInst);
        Drive(robotaxi, "Grand tour", "A-B", 8f, 13f, tp, ts, tr, VizTracked.Kind.Ego);

        // ---- passenger cars (Sketchfab "Generic passenger car pack" by Comrade1280, CC-BY)
        var carPack = Load($"{VehicleDir}/generic_passenger_car_pack/scene.gltf");
        if (carPack)
        {
            var inst = Unpacked(carPack);
            var cars = SplitPassengerPack(inst);
            var plan = new (string name, string route, string street, float s, float speed, float length)[]
            {
                ("Sedan", "Perimeter", "C-D", 14f, 8f, 4.7f), ("Offroad", "West loop", "H-A", 72f, 9f, 4.6f),
                ("Hatchback", "East loop", "D-E", 80f, 8f, 4.1f), ("minivan", "Perimeter", "G-H", 40f, 8.5f, 4.9f),
                ("Pickup", "Grand tour", "F-G", 20f, 10f, 5.3f), ("Compact", "East loop", "C-D", 38f, 9f, 4.0f),
                ("Coupe", "West loop", "B-G", 40f, 11f, 4.5f),
            };
            foreach (var (name, route, street, s, speed, length) in plan)
            {
                var parts = cars.FirstOrDefault(c => c.name.ToLower().Contains(name.ToLower()));
                if (parts == null) { Debug.LogWarning("CityDressing: no car named " + name); continue; }
                var car = MakeVehicle(parts, length, FlipPassengerPack, out var p, out var st, out var r);
                car.name = $"{name} ({route})";
                Drive(car, route, street, s, speed, p, st, r, VizTracked.Kind.Vehicle);
            }
            Object.DestroyImmediate(inst);
        }

        // ---- box truck (Sketchfab "LCT 3000 '95" by Daniel Zhabotinsky, CC-BY)
        var truckModel = Load($"{VehicleDir}/lct_3000_95_-_low_poly_model/scene.gltf");
        if (truckModel)
        {
            var inst = Unpacked(truckModel);
            var rootNode = inst.GetComponentsInChildren<Transform>().First(t => t.name == "RootNode");
            var parts = new VehicleParts { name = "Box Truck (Perimeter)", frame = rootNode };
            foreach (Transform t in rootNode)
                (t.name.Contains("WheelStock") ? parts.wheels : parts.body).Add(t);
            var truck = MakeVehicle(parts, 6.2f, FlipBoxTruck, out var p, out var st, out var r);
            Object.DestroyImmediate(inst);
            Drive(truck, "Perimeter", "E-F", 30f, 6.5f, p, st, r, VizTracked.Kind.Truck);
        }

        // ---- service vehicles (Sketchfab "Generic civil service vehicles pack", CC-BY):
        //      two stopped in the right lane, the rest parked in a depot in the east block
        var civil = Load($"{VehicleDir}/generic_civil_service_vehicles_pack/scene.gltf");
        if (civil)
        {
            var inst = Unpacked(civil);
            GameObject Civil(string node, string label, float length)
            {
                var t = inst.GetComponentsInChildren<Transform>().FirstOrDefault(x => x.name == node);
                if (!t) return null;
                var v = MakeVehicle(new VehicleParts { name = label, frame = t, body = { t } }, length, FlipCivilPack, out _, out _, out _);
                v.AddComponent<VizTracked>().kind = VizTracked.Kind.Truck;
                v.transform.SetParent(parent);
                return v;
            }
            void InLane(GameObject v, string street, float s)
            {
                if (!v) return;
                var p = net.streets[street];
                v.transform.SetPositionAndRotation(p.Point(s, 0), Quaternion.LookRotation(p.Tangent(s)));
            }
            InLane(Civil("garbage_truck", "Garbage Truck (double-parked)", 8f), "H-A", 46f);
            InLane(Civil("citybus", "City Bus (at stop)", 11f), "B-C", 33f);   // well clear of junction B

            var lot = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Object.DestroyImmediate(lot.GetComponent<Collider>());
            lot.name = "Depot Lot";
            lot.transform.SetParent(parent);
            lot.transform.position = new Vector3(80f, CityNetwork.Curb + 0.01f, 0f);
            lot.transform.localScale = new Vector3(3.2f, 1f, 6.6f);
            lot.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Generated/Asphalt.mat");
            float x = 67f;
            foreach (var (node, label, length) in new[] { ("police_sedan", "Police Car (parked)", 4.9f), ("taxi", "Taxi (parked)", 4.8f),
                     ("ambulance", "Ambulance (parked)", 6.5f), ("fire_truck", "Fire Truck (parked)", 9.5f), ("school_bus", "School Bus (parked)", 11f),
                     ("towtruck", "Tow Truck (parked)", 7f), ("postvan", "Post Van (parked)", 5.5f), ("rdservtruck", "Service Truck (parked)", 7f) })
            {
                var v = Civil(node, label, length);
                if (!v) continue;
                v.transform.SetPositionAndRotation(new Vector3(x, CityNetwork.Curb + 0.01f, 0f), Quaternion.identity);
                x += 3.6f;
            }
            Object.DestroyImmediate(inst);
        }
        return robotaxi;
    }

    // ------------------------------------------------------------------ props

    // A Poly Haven model at real-world size, grounded, with a collider; null if missing.
    public static GameObject Prop(string id, string name, bool collider = true)
    {
        var model = Load($"{PH}/models/{id}/{id}.gltf");
        if (!model) return null;
        var root = new GameObject(name);
        var vis = (GameObject)PrefabUtility.InstantiatePrefab(model);
        vis.name = "Model";
        vis.transform.SetParent(root.transform, false);
        var b = RendererBounds(new[] { vis.transform });
        vis.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        if (collider)
        {
            b = RendererBounds(new[] { vis.transform });
            var col = root.AddComponent<BoxCollider>();
            col.center = b.center;
            col.size = b.size;
        }
        return root;
    }

    // A prop from the City Props Collection vol. 2 (Sketchfab, CC-BY; split per prop with
    // tools/blender_export_nodes.py), grounded, with `yaw` turning its front to the root's forward.
    static GameObject CityProp(string id, string name, float yaw = 0f, bool collider = true)
    {
        var model = Load($"Assets/Props/{id}.glb");
        if (!model) return null;
        var root = new GameObject(name);
        var vis = (GameObject)PrefabUtility.InstantiatePrefab(model);
        vis.name = "Model";
        vis.transform.SetParent(root.transform, false);
        vis.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        var b = RendererBounds(new[] { vis.transform });
        vis.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        if (collider)
        {
            b = RendererBounds(new[] { vis.transform });
            var col = root.AddComponent<BoxCollider>();
            col.center = b.center;
            col.size = b.size;
        }
        return root;
    }

    // Places a prop on the sidewalk at `lateral` with its front facing the road.
    static void FacingRoad(GameObject go, RoadPath road, float s, float lateral, Transform parent) =>
        Place(go, road, s, lateral, lateral > 0f ? -90f : 90f, parent, CityNetwork.Curb);

    // Bus shelter, bins, boards, signs, beacons, manholes and a subway entrance: the sidewalks get
    // a varied row of furniture behind the walking line (people walk at 5.7 m, buildings start at
    // 8.5 m) that never lands on a lamp, bench, signal, crossing or another prop.
    static void StreetProps(CityNetwork net, Transform street)
    {
        var taken = new List<Vector3>();
        foreach (Transform t in street) taken.Add(t.position);
        foreach (var tl in Object.FindObjectsByType<TrafficLight>(FindObjectsSortMode.None)) taken.Add(tl.transform.position);
        var crossings = Object.FindObjectsByType<Crosswalk>(FindObjectsSortMode.None);
        bool Free(Vector3 p, float r)
        {
            foreach (var q in taken) if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(q.x, 0f, q.z)) < r) return false;
            foreach (var cw in crossings)
                if (Vector3.Distance(p, cw.Center) < 9f) return false;
            return true;
        }

        // The bus stop at the parked city bus (outer side of B-C).
        var bc = net.streets["B-C"];
        FacingRoad(CityProp("bus_stop", "Bus Shelter", 90f), bc, 33f, 6.1f, street);
        FacingRoad(CityProp("information", "Timetable Board"), bc, 38.5f, 6.6f, street);
        taken.Add(bc.PointAtOffset(33f, 6.1f));
        taken.Add(bc.PointAtOffset(38.5f, 6.6f));

        // Zebra-crossing beacons on both kerbs just past the stripes (the signal stands before them).
        foreach (var cw in crossings)
            foreach (float side in new[] { 1f, -1f })
                FacingRoad(CityProp("crosswalk_lamp", "Crossing Beacon"), cw.road, cw.s1 + 0.8f, side * 4.45f, street);

        // A varied row of small furniture along every street, both sides.
        var kinds = new (string id, string name, float yaw)[]
        {
            ("trash", "Litter Bin", 0f), ("information", "Information Board", 0f), ("trash_bin1", "Trash Bin", 0f),
            ("planter_pot", "Planter Pot", 0f), ("utility", "Utility Box", 0f), ("sign", "Shop Sign", 0f),
            ("trash_bin0", "Recycling Bin", 0f), ("directory", "City Directory", 0f), ("public_parking", "Parking Sign", 0f),
            ("clock", "Street Clock", 0f), ("barrier", "Bollard", 0f),
        };
        int k = 0;
        foreach (var kv in net.streets)
        {
            var p = kv.Value;
            foreach (float side in new[] { 1f, -1f })
                for (float s = side > 0f ? 11f : 25f; s < p.Length - 4f; s += 17f)
                {
                    float lat = side * 6.75f;
                    if (!Free(p.PointAtOffset(s, lat), 3f)) continue;
                    var (id, name, yaw) = kinds[k++ % kinds.Length];
                    FacingRoad(CityProp(id, name, yaw), p, s, lat, street);
                    taken.Add(p.PointAtOffset(s, lat));
                }
        }

        // Manhole covers in the lanes (flush with the asphalt, nothing to collide with).
        string[] covers = { "manhole_cover0", "manhole_cover1", "manhole_cover2" };
        foreach (var kv in net.streets)
            for (float s = 18f; s < kv.Value.Length - 6f; s += 33f)
            {
                var m = CityProp(covers[k++ % covers.Length], "Manhole Cover", 0f, false);
                Place(m, kv.Value, s, kv.Value.LaneOffset(k % 2), 0f, street, 0.022f);
            }

        // A subway entrance in the clearing of the park, guarded by bollards.
        var subway = CityProp("subway", "Subway Entrance", 90f);
        if (subway)
        {
            subway.transform.SetPositionAndRotation(new Vector3(0f, CityNetwork.Curb, 0f), Quaternion.identity);
            subway.transform.SetParent(street);
            foreach (float x in new[] { -3f, 0f, 3f })
            {
                var b = CityProp("barrier", "Bollard");
                if (!b) continue;
                b.transform.localScale = Vector3.one * 0.6f;
                b.transform.SetPositionAndRotation(new Vector3(x, CityNetwork.Curb, -7f), Quaternion.identity);
                b.transform.SetParent(street);
            }
        }
    }

    static void Place(GameObject go, RoadPath road, float s, float lateral, float yawFromRoad, Transform parent, float y = 0f)
    {
        if (!go) return;
        go.transform.position = road.PointAtOffset(s, lateral) + Vector3.up * y;
        go.transform.rotation = Quaternion.LookRotation(road.Tangent(s)) * Quaternion.Euler(0f, yawFromRoad, 0f);
        go.transform.SetParent(parent);
    }

    // Street furniture, trees, road works and the obstacles the demo can drop with B (returned).
    public static GameObject[] DressCity(CityNetwork net, Transform parent, GameObject genexBarrier)
    {
        float curb = CityNetwork.Curb;
        var street = new GameObject("Street Furniture").transform;
        street.SetParent(parent);
        var rng = new System.Random(5);

        // Lamps on both sidewalks of every street, hydrants and benches on some.
        foreach (var kv in net.streets)
        {
            var p = kv.Value;
            for (float s = 6f; s < p.Length - 4f; s += 28f)
            {
                Place(Prop("street_lamp_01", "Street Lamp"), p, s, 6.3f, -90f, street, curb);
                Place(Prop("street_lamp_01", "Street Lamp"), p, Mathf.Min(s + 14f, p.Length - 2f), -6.3f, 90f, street, curb);
            }
            if (p.Length > 60f) Place(Prop("fire_hydrant", "Fire Hydrant"), p, p.Length * 0.5f + 8f, 4.6f, 0f, street, curb);
        }
        foreach (var (s, name) in new[] { (12f, "C-D"), (40f, "E-F"), (12f, "G-H"), (70f, "H-A") })
            Place(Prop("modular_street_seating", "Bench"), net.streets[name], s, 5.9f, 180f, street, curb);
        foreach (var (s, name) in new[] { (30f, "B-C"), (30f, "F-G") })
            Place(Prop("planter_box_01", "Planter"), net.streets[name], s, -5.8f, 0f, street, curb);

        // Trees: a park in the centre block and a courtyard in the west block.
        var trees = new GameObject("Trees").transform;
        trees.SetParent(parent);
        string[] ids = { "island_tree_01", "island_tree_02" };
        void Tree(Vector3 pos, float scale)
        {
            var t = Prop(ids[rng.Next(ids.Length)], "Tree", false);
            if (!t) return;
            t.transform.localScale *= scale;
            t.transform.SetPositionAndRotation(pos + Vector3.up * curb, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
            t.transform.SetParent(trees);
        }
        for (int i = 0; i < 16; i++)
        {
            var pos = new Vector3(((float)rng.NextDouble() * 2f - 1f) * 26f, 0f, ((float)rng.NextDouble() * 2f - 1f) * 44f);
            if (Mathf.Abs(pos.x) < 8f && Mathf.Abs(pos.z) < 12f) continue;   // keep a clearing in the middle
            Tree(pos, 0.8f + (float)rng.NextDouble() * 0.4f);
        }
        for (int i = 0; i < 6; i++)
            Tree(new Vector3(-80f + ((float)rng.NextDouble() * 2f - 1f) * 12f, 0f, ((float)rng.NextDouble() * 2f - 1f) * 25f), 0.8f + (float)rng.NextDouble() * 0.3f);
        foreach (var (s, name) in new[] { (10f, "B-G"), (60f, "B-G"), (10f, "F-C"), (60f, "F-C") })
            Place(Prop("modular_street_seating", "Bench"), net.streets[name], s, name == "B-G" ? 6.5f : 6.5f, 180f, street, curb);
        var parked = Prop("covered_car", "Covered Car");
        if (parked) { parked.transform.SetPositionAndRotation(new Vector3(-80f, curb, 12f), Quaternion.Euler(0f, 75f, 0f)); parked.transform.SetParent(street); }
        StreetProps(net, street);

        // Road works closing the right lane of the east avenue (D-E) from s = 16 to 28:
        // striped barriers at both ends, concrete barriers between them. The left lane stays open.
        var works = new GameObject("Road Works").transform;
        works.SetParent(parent);
        var avenue = net.streets["D-E"];
        void Striped(float s)
        {
            if (!genexBarrier) return;
            var g = Object.Instantiate(genexBarrier);
            g.name = "Road Barrier";
            Place(g, avenue, s, avenue.LaneOffset(0), 0f, works);
        }
        Striped(16f);
        Place(Prop("concrete_road_barrier", "Concrete Barrier"), avenue, 20f, avenue.LaneOffset(0), 0f, works);
        Place(Prop("concrete_road_barrier", "Concrete Barrier"), avenue, 24f, avenue.LaneOffset(0), 0f, works);
        Striped(28f);
        foreach (Transform t in works) t.gameObject.AddComponent<VizTracked>().kind = VizTracked.Kind.Obstacle;

        // Things the demo can drop in front of the car with B (disabled templates).
        var templates = new List<GameObject>();
        var drop = new GameObject("Drop Templates").transform;
        drop.SetParent(parent);
        void Template(GameObject go)
        {
            if (!go) return;
            go.name = go.name + " (template)";
            if (!go.GetComponent<VizTracked>()) go.AddComponent<VizTracked>().kind = VizTracked.Kind.Obstacle;
            go.transform.SetParent(drop);
            go.SetActive(false);
            templates.Add(go);
        }
        Template(genexBarrier);
        Template(Prop("concrete_road_barrier_02", "Concrete Barrier"));
        Template(Prop("Barrel_01", "Barrel"));
        Template(Prop("cardboard_box_01", "Box"));
        return templates.ToArray();
    }

    // ------------------------------------------------------------------ sky and surfaces

    public static void ApplySky()
    {
        var hdr = Directory.Exists($"{PH}/hdri") ? Directory.GetFiles($"{PH}/hdri", "*.hdr").FirstOrDefault() : null;
        if (hdr == null) return;
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(hdr.Replace('\\', '/'));
        var mat = new Material(Shader.Find("Skybox/Panoramic"));
        mat.SetTexture("_MainTex", tex);
        mat.SetFloat("_Mapping", 1f);
        mat.SetFloat("_ImageType", 0f);
        mat.SetFloat("_Exposure", 1.1f);
        AssetDatabase.DeleteAsset("Assets/Generated/Sky.mat");
        AssetDatabase.CreateAsset(mat, "Assets/Generated/Sky.mat");
        RenderSettings.skybox = mat;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
    }

    // Photo-scanned surface: base colour + normal map from a Poly Haven texture folder.
    public static void Surface(Material m, string id, Vector2 tiling, Color? tint = null)
    {
        string dir = $"{PH}/textures/{id}";
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir).Select(p => p.Replace('\\', '/')).Where(p => !p.EndsWith(".meta")).ToArray();
        var diff = files.FirstOrDefault(f => f.Contains("_Diffuse"));
        var nor = files.FirstOrDefault(f => f.Contains("_nor_gl"));
        if (diff != null)
        {
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(diff));
            m.SetColor("_BaseColor", tint ?? Color.white);
        }
        if (nor != null)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(nor);
            if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
            m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nor));
            m.EnableKeyword("_NORMALMAP");
        }
        m.SetTextureScale("_BaseMap", tiling);
        EditorUtility.SetDirty(m);
    }
}
