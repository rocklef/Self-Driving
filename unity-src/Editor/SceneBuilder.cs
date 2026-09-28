using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: Self Driving > Build Demo Scene. Generates the whole demo city: the downtown street grid
// (CityNetwork), block pads and sidewalks, markings, buildings, trees and street furniture,
// signalised junctions and crossings with pedestrians, the robotaxi and traffic on their routes,
// camera, lighting, the autopilot visualisation and the dashboard.
public static class SceneBuilder
{
    const string ModelDir = "Assets/Models", TexDir = "Assets/Textures";
    const string GenDir = "Assets/Generated", ScenePath = "Assets/Scenes/SelfDrivingDemo.unity";

    // Traffic-light lamp heights as a fraction of the model's height.
    static readonly float[] LampHeights = { 0.664f, 0.58f, 0.498f };

    const float HalfRoad = CityNetwork.HalfRoad, CurbHeight = CityNetwork.Curb;

    [MenuItem("Self Driving/Build Demo Scene")]
    public static void Build()
    {
        Directory.CreateDirectory(GenDir);
        Directory.CreateDirectory("Assets/Scenes");
        foreach (var old in Directory.GetFiles(GenDir, "*.asset")) AssetDatabase.DeleteAsset(old.Replace('\\', '/'));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var carModel = LoadModel("robotaxi_wheels");   // wheels split out in Blender (tools/blender_split_wheels.py)
        var lightModel = LoadModel("traffic-light");
        var barrierModel = LoadModel("striped-plastic-road");
        var buildingModel = LoadModel("building");
        if (!carModel || !lightModel || !barrierModel || !buildingModel)
        {
            Debug.LogError("SceneBuilder: a model is missing from Assets/Models, or glTFast is not installed so .glb files are not imported.");
            return;
        }

        var mats = CreateMaterials();
        CityDressing.Surface(mats["Asphalt"], "asphalt_02", new Vector2(2f, 2f), new Color(0.8f, 0.8f, 0.8f));
        CityDressing.Surface(mats["Sidewalk"], "concrete_pavement_02", new Vector2(1.5f, 1.5f));
        CityDressing.Surface(mats["Grass"], "leafy_grass", new Vector2(150f, 150f), new Color(0.55f, 0.85f, 0.4f));   // green up the dry-looking scan
        CityDressing.Surface(mats["Lawn"], "leafy_grass", new Vector2(1f, 1f), new Color(0.5f, 0.85f, 0.38f));

        // ---- streets, junctions, blocks ----
        var roadRoot = new GameObject("Road Network").transform;
        var net = BuildRoads(roadRoot, mats);
        // Junction markers. B and F are splits: one route goes straight on while another turns left
        // across the left lane.
        foreach (var node in CityNetwork.Nodes.Keys)
        {
            bool split = node is "B" or "F";
            var j = new GameObject((split ? "Split Junction " : "Junction ") + node).AddComponent<Junction>();
            j.split = split;
            j.transform.SetParent(roadRoot);
            j.transform.position = CityNetwork.Nodes[node];
        }

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(60f, 1f, 60f);
        ground.GetComponent<Renderer>().sharedMaterial = mats["Grass"];
        Object.DestroyImmediate(ground.GetComponent<Collider>());

        // ---- signals: two merge junctions (phased) and three signalised zebra crossings ----
        var lights = new GameObject("Traffic Signals").transform;
        const float green = 12f, yellow = 3f, allRed = 2f, cycle = 2f * (green + yellow + allRed);
        foreach (var (through, side) in new[] { ("B-C", "F-C"), ("F-G", "B-G") })
        {
            var a = net.streets[through];
            var b = net.streets[side];
            MakeTrafficLight(a, a.Length - 1f, lightModel, green, yellow, cycle - green - yellow, 0f, lights, mats);
            MakeTrafficLight(b, b.Length - 1f, lightModel, green, yellow, cycle - green - yellow, -(green + yellow + allRed), lights, mats);
        }
        var crossings = new List<Crosswalk>();
        foreach (var (street, stopS, offset) in new[] { ("A-B", 22f, 0f), ("G-H", 22f, 7f), ("D-E", 58f, 14f) })
        {
            var p = net.streets[street];
            crossings.Add(MakeCrosswalk(p, stopS, MakeTrafficLight(p, stopS, lightModel, 10f, 3f, 12f, offset, lights, mats), lights, mats));
        }

        // ---- pedestrians around each crossing ----
        var people = new GameObject("Pedestrians").transform;
        var pedTemplates = MakePedestrianTemplates(people, mats);
        var rng = new System.Random(11);
        int n = 0;
        foreach (var cw in crossings)
            foreach (float side in new[] { 1f, -1f, 1f, -1f, 1f })
            {
                var p = Object.Instantiate(pedTemplates[n++ % pedTemplates.Length], people);
                p.name = "Pedestrian " + n;
                float s = Mathf.Min(cw.s1 + 2f + (float)rng.NextDouble() * 14f, cw.road.Length - 2f);
                p.transform.position = cw.road.PointAtOffset(s, side * 5.7f) + Vector3.up * CurbHeight;
                p.transform.rotation = Quaternion.LookRotation(cw.road.Tangent(s));
                var ped = p.GetComponent<Pedestrian>();
                ped.road = cw.road;
                ped.crosswalk = cw;
                ped.walkSpeed = 1.15f + (float)rng.NextDouble() * 0.35f;
                p.SetActive(true);
            }

        // ---- buildings: outer ring and the west / east blocks, all facing the street ----
        var city = new GameObject("City").transform;
        int count = PlaceBuildings(net, buildingModel, city);

        // ---- street furniture, trees, road works, droppable obstacles ----
        var template = MakeProp(barrierModel, "Road Barrier", 2.2f, false, false);
        var obstacleTemplates = CityDressing.DressCity(net, city, template);

        // ---- vehicles: the robotaxi, traffic on its routes, parked and depot vehicles ----
        var vehicles = new GameObject("Vehicles").transform;
        var robotaxi = CityDressing.BuildTraffic(net, carModel, vehicles);
        var egoRoute = robotaxi.GetComponent<AutoPilot>().road;

        // ---- camera, light, atmosphere ----
        var cam = Camera.main;
        cam.farClipPlane = 900f;
        cam.fieldOfView = 55f;
        cam.transform.position = robotaxi.transform.position + new Vector3(0f, 4f, -9f);
        var rig = cam.gameObject.AddComponent<CameraRig>();
        rig.target = robotaxi.transform;

        var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).First(l => l.type == LightType.Directional);
        sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
        sun.intensity = 1.4f;
        sun.shadows = LightShadows.Soft;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0022f;
        RenderSettings.fogColor = new Color(0.76f, 0.83f, 0.9f);
        CityDressing.ApplySky();

        var viz = new GameObject("Driving Visualization").AddComponent<TeslaViz>();
        viz.ego = robotaxi.GetComponent<AutoPilot>();
        viz.road = egoRoute;
        viz.litShader = Shader.Find("Universal Render Pipeline/Lit");
        viz.unlitShader = Shader.Find("Universal Render Pipeline/Unlit");

        var demo = new GameObject("Demo").AddComponent<DemoController>();
        demo.robotaxi = robotaxi.GetComponent<AutoPilot>();
        demo.cameraRig = rig;
        demo.road = egoRoute;
        demo.viz = viz;
        demo.obstacleTemplates = obstacleTemplates;
        demo.pedestrianTemplates = pedTemplates;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log($"SceneBuilder: built {ScenePath} ({CityNetwork.Edges.Length} streets, {net.routes.Count} routes, {count} buildings).");
    }

    // Entry point for: Unity -batchmode -executeMethod SceneBuilder.BuildBatch -quit
    public static void BuildBatch()
    {
        AssetDatabase.Refresh();
        Build();
    }

    // ------------------------------------------------------------------ roads and blocks

    static CityNetwork BuildRoads(Transform root, Dictionary<string, Material> mats)
    {
        var asphalt = new MeshBuilder();
        var lowAsphalt = new MeshBuilder();
        var marks = new MeshBuilder();
        var walks = new MeshBuilder();
        var curbs = new MeshBuilder();
        var net = new CityNetwork();
        net.Build(root, (name, p, configure) =>
        {
            var m = new CityNetwork.RoadMesh();
            configure(m);
            float L = p.Length;
            if (m.asphalt) (m.lower ? lowAsphalt : asphalt).Strip(p, 0f, L, -HalfRoad, HalfRoad, m.lower ? 0.012f : 0.02f, 8f, 1f);
            if (m.edgeLines)
            {
                marks.Strip(p, 0f, L, HalfRoad - 0.4f, HalfRoad - 0.25f, 0.03f, 1f, 1f);
                marks.Strip(p, 0f, L, -HalfRoad + 0.25f, -HalfRoad + 0.4f, 0.03f, 1f, 1f);
            }
            if (m.divider)
                for (float s = 1f; s < L - 3f; s += 9f) marks.Strip(p, s, s + 3f, -0.08f, 0.08f, 0.03f, 1f, 1f);
            if (m.arrows)
                for (int lane = 0; lane < 2; lane++) marks.Arrow(p, L * 0.35f, lane, 0.03f);
            if (m.outerWalk)
            {
                walks.Strip(p, 0f, L, HalfRoad, CityNetwork.OuterWalk, CurbHeight, 3f, 1f);
                curbs.Wall(p, 0f, L, HalfRoad, 0.02f, CurbHeight);
            }
        });

        // Raised concrete block pads with curbs; the centre block gets a lawn.
        var temp = new GameObject("temp");
        foreach (var (name, x0, x1, z0, z1, r) in CityNetwork.Blocks)
        {
            var outline = CityNetwork.RoundedRect(x0, x1, z0, z1, r, CurbHeight);
            walks.Fan(outline);
            var edge = temp.AddComponent<RoadPath>();
            edge.SetPoints(outline, true);
            curbs.Wall(edge, 0f, edge.Length, 0f, 0.02f, CurbHeight);
            Object.DestroyImmediate(edge);
        }
        Object.DestroyImmediate(temp);
        var (_, px0, px1, pz0, pz1, _) = CityNetwork.Blocks[1];
        var lawn = new MeshBuilder();
        lawn.Fan(CityNetwork.RoundedRect(px0 + 4f, px1 - 4f, pz0 + 4f, pz1 - 4f, 6f, CurbHeight + 0.01f), 4f);

        MeshObject("Asphalt", asphalt, mats["Asphalt"], root);
        MeshObject("Asphalt", lowAsphalt, mats["Asphalt"], root);
        MeshObject("Lane Markings", marks, mats["Marking"], root);
        MeshObject("Sidewalks", walks, mats["Sidewalk"], root);
        MeshObject("Curbs", curbs, mats["Curb"], root);
        MeshObject("Park Lawn", lawn, mats["Lawn"], root);
        return net;
    }

    // Buildings split out of the Sketchfab packs (tools/blender_split_buildings.py), each with its
    // name, relative weight, how much it may be scaled up, and the model yaw that turns its facade
    // to the root's forward (Unity +Z; Blender -Y).
    const string BuildingDir = "Assets/Buildings";
    static readonly (string file, float weight, float maxScale, float yaw)[] BuildingTypes =
    {
        ("brooklyn_row", 0.6f, 1.0f, 180f), ("brick_shop", 1f, 1.15f, 90f), ("eu_station", 0.35f, 1.0f, 0f),
        ("eu_townhall", 0.5f, 1.1f, 90f), ("eu_corner_shop", 1.2f, 1.3f, 0f), ("eu_gabled_row", 1f, 1.25f, 0f),
        ("eu_white_townhouse", 1f, 1.4f, 0f), ("eu_cafe_house", 1.2f, 1.25f, 0f), ("eu_hotel", 1f, 1.2f, 0f),
        ("eu_red_shop", 1.2f, 1.3f, 0f), ("eu_pavilion", 0.3f, 1.2f, 0f),
    };

    // A row of different buildings along each street side: facades on the building line behind the
    // sidewalk, varied type, scale and gaps, so no two neighbours look alike. Towers (the Genex
    // office) break up the skyline. Nothing may overlap another building or the reserved areas.
    static int PlaceBuildings(CityNetwork net, GameObject tower, Transform parent)
    {
        var rng = new System.Random(21);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var types = BuildingTypes
            .Select(t => (t.file, t.weight, t.maxScale, t.yaw, model: AssetDatabase.LoadAssetAtPath<GameObject>($"{BuildingDir}/{t.file}.glb")))
            .Where(t => t.model).ToList();
        // footprint (facade width, depth, height) of each model with its facade to +Z
        var dims = new Dictionary<string, Vector3>();
        foreach (var t in types)
        {
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(t.model);
            probe.transform.rotation = Quaternion.Euler(0f, t.yaw, 0f);
            dims[t.file] = WorldBounds(probe).size;
            Object.DestroyImmediate(probe);
        }
        Debug.Log("SceneBuilder: building types " + string.Join(", ", dims.Select(d => $"{d.Key} {d.Value.x:0}x{d.Value.z:0}x{d.Value.y:0}")));

        // Occupied ground (x0, x1, z0, z1): the depot lot and the west courtyard trees, plus every building.
        var used = new List<Rect> { Rect.MinMaxRect(62f, -36f, 98f, 36f), Rect.MinMaxRect(-94f, -28f, -66f, 28f) };
        bool Free(Rect r) => used.All(u => !u.Overlaps(Rect.MinMaxRect(r.xMin + 0.3f, r.yMin + 0.3f, r.xMax - 0.3f, r.yMax - 0.3f)));

        int count = 0;
        string previous = "";
        void Row(string street, float side, float maxDepth)
        {
            const float Front = 8.5f;
            var p = net.streets[street];
            float s = R(0f, 3f);
            while (s < p.Length - 4f)
            {
                bool placed = false;
                for (int attempt = 0; attempt < 8 && !placed; attempt++)
                {
                    GameObject model;
                    Vector3 size;
                    float scale, yaw = 0f;
                    string file;
                    if (rng.NextDouble() < 0.16)   // a tower now and then
                    {
                        file = "tower";
                        model = tower;
                        var probe = MakeProp(tower, "probe", R(20f, 32f), true, false);
                        size = probe.GetComponent<BoxCollider>().size;
                        Object.DestroyImmediate(probe);
                        float fp = Mathf.Max(size.x, size.z);
                        scale = fp > 15f ? 15f / fp : 1f;
                        size *= scale;
                        size.x = size.z = Mathf.Max(size.x, size.z);
                    }
                    else
                    {
                        float total = types.Sum(t => t.weight), pick = R(0f, total);
                        var t = types.First(x => (pick -= x.weight) <= 0f || x.file == types[^1].file);
                        file = t.file;
                        model = t.model;
                        yaw = t.yaw;
                        scale = R(0.95f, t.maxScale);
                        size = dims[file] * scale;
                        if (size.z > maxDepth) { scale *= maxDepth / size.z; size = dims[file] * scale; }
                        if (scale < 0.8f) continue;
                    }
                    if (file == previous || s + size.x > p.Length + 1f) continue;

                    float sc = s + size.x * 0.5f;
                    Vector3 facing = -side * p.Right(sc);
                    Vector3 centre = p.PointAtOffset(sc, side * (Front + size.z * 0.5f));
                    Vector3 across = Vector3.Cross(Vector3.up, facing) * size.x * 0.5f, deep = facing * size.z * 0.5f;
                    var c = new[] { centre + across + deep, centre + across - deep, centre - across + deep, centre - across - deep };
                    var rect = Rect.MinMaxRect(c.Min(v => v.x), c.Min(v => v.z), c.Max(v => v.x), c.Max(v => v.z));
                    if (!Free(rect)) continue;

                    GameObject b;
                    if (file == "tower") b = MakeProp(tower, "Tower " + (count + 1), size.y, true, false);
                    else
                    {
                        b = new GameObject($"{file} {count + 1}");
                        var vis = (GameObject)PrefabUtility.InstantiatePrefab(model);
                        vis.name = "Model";
                        vis.transform.SetParent(b.transform, false);
                        vis.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                        vis.transform.localScale *= scale;
                        var wb = WorldBounds(vis);
                        vis.transform.position += new Vector3(-wb.center.x, -wb.min.y, -wb.center.z);
                        wb = WorldBounds(vis);
                        var col = b.AddComponent<BoxCollider>();
                        col.center = wb.center;
                        col.size = wb.size;
                    }
                    b.transform.SetParent(parent);
                    b.transform.SetPositionAndRotation(new Vector3(centre.x, 0f, centre.z), Quaternion.LookRotation(facing));
                    used.Add(rect);
                    count++;
                    previous = file;
                    s += size.x + R(0.4f, 3.5f);
                    placed = true;
                }
                if (!placed) s += 3f;
            }
        }
        foreach (var street in new[] { "A-B", "B-C", "C-D", "D-E", "E-F", "F-G", "G-H", "H-A" }) Row(street, 1f, 40f);   // outside the city
        foreach (var street in new[] { "A-B", "B-G", "G-H", "H-A" }) Row(street, -1f, 18f);                          // west block
        foreach (var street in new[] { "C-D", "E-F", "F-C", "D-E" }) Row(street, -1f, 18f);                          // east block around the depot
        return count;
    }

    // ------------------------------------------------------------------ helpers

    static GameObject LoadModel(string keyword)
    {
        var path = Directory.GetFiles(ModelDir, "*.glb").FirstOrDefault(p => Path.GetFileName(p).Contains(keyword));
        return path == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path.Replace('\\', '/'));
    }

    static Bounds WorldBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    // Wraps a model in a root object: scaled to `size` (height, or longest side), sitting on y=0,
    // centred, with a box collider. `longAlongZ` turns the model so its long side runs along
    // the root's forward axis; otherwise the long side runs across it.
    public static GameObject MakeProp(GameObject prefab, string name, float size, bool byHeight, bool longAlongZ, float extraYaw = 0f)
    {
        var root = new GameObject(name);
        var vis = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        vis.name = "Model";
        vis.transform.SetParent(root.transform, false);

        var b = WorldBounds(vis);
        bool longIsX = b.size.x >= b.size.z;
        float yaw = longIsX == longAlongZ ? 90f : 0f;
        vis.transform.localRotation = Quaternion.Euler(0f, yaw + extraYaw, 0f);

        b = WorldBounds(vis);
        float current = byHeight ? b.size.y : Mathf.Max(b.size.x, b.size.z);
        vis.transform.localScale *= size / Mathf.Max(current, 1e-4f);
        b = WorldBounds(vis);
        vis.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        b = WorldBounds(vis);

        var col = root.AddComponent<BoxCollider>();
        col.center = b.center;
        col.size = b.size;
        return root;
    }

    // A signal head on the right-hand kerb of `street`, stopping traffic at stopS.
    static TrafficLight MakeTrafficLight(RoadPath street, float stopS, GameObject model, float green, float yellow, float red, float offset, Transform parent, Dictionary<string, Material> mats)
    {
        float s = Mathf.Min(stopS + 1.5f, street.Length);
        var go = MakeProp(model, "Traffic Light", 5f, true, true);   // lenses point along the long axis
        go.transform.SetParent(parent);
        go.transform.position = street.PointAtOffset(s, HalfRoad + 1.2f) + Vector3.up * CurbHeight;
        go.transform.rotation = Quaternion.LookRotation(-street.Tangent(s));   // faces oncoming cars

        var tl = go.AddComponent<TrafficLight>();
        tl.stopPoint = street.Center(stopS);
        tl.approachDir = street.Tangent(stopS);
        tl.greenTime = green;
        tl.yellowTime = yellow;
        tl.redTime = red;
        tl.startOffset = offset;
        var box = go.GetComponent<BoxCollider>();
        float front = box.center.z + box.size.z * 0.5f - 0.1f;   // just inside the lens hood
        var lamps = new Renderer[3];
        string[] names = { "Red", "Yellow", "Green" };
        for (int i = 0; i < 3; i++)
        {
            var lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(lamp.GetComponent<Collider>());
            lamp.name = names[i] + " Lamp";
            lamp.transform.SetParent(go.transform, false);
            lamp.transform.localPosition = new Vector3(box.center.x, box.size.y * LampHeights[i], front);
            lamp.transform.localScale = Vector3.one * 0.3f;
            lamp.GetComponent<Renderer>().sharedMaterial = mats["Lamp" + names[i]];
            lamps[i] = lamp.GetComponent<Renderer>();
        }
        tl.redLamp = lamps[0]; tl.yellowLamp = lamps[1]; tl.greenLamp = lamps[2];

        var glowGo = new GameObject("Glow");
        glowGo.transform.SetParent(go.transform, false);
        glowGo.transform.localPosition = new Vector3(box.center.x, box.size.y * LampHeights[1], front + 0.4f);
        var glow = glowGo.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.range = 3f;
        glow.intensity = 1.2f;
        tl.glow = glow;

        // stop line across both lanes
        var mb = new MeshBuilder();
        mb.Strip(street, stopS - 0.25f, stopS + 0.25f, -HalfRoad + 0.3f, HalfRoad - 0.3f, 0.035f, 1f, 0.5f);
        MeshObject("Stop Line", mb, mats["Marking"], parent);
        return tl;
    }

    // Zebra crossing just past the stop line, with walk / don't-walk signals on both kerbs.
    static Crosswalk MakeCrosswalk(RoadPath street, float stopS, TrafficLight tl, Transform parent, Dictionary<string, Material> mats)
    {
        var go = new GameObject("Crosswalk");
        go.transform.SetParent(parent);
        var cw = go.AddComponent<Crosswalk>();
        cw.signal = tl;
        cw.road = street;
        cw.s0 = stopS + 2.5f;
        cw.s1 = stopS + 6.5f;

        var stripes = new MeshBuilder();
        for (float lat = -HalfRoad + 0.35f; lat < HalfRoad - 0.5f; lat += 1.0f)
            stripes.Strip(street, cw.s0, cw.s1, lat, lat + 0.5f, 0.033f, 1f, 0.5f);
        MeshObject("Zebra Stripes", stripes, mats["Marking"], go.transform);

        var walk = new List<Renderer>();
        var dontWalk = new List<Renderer>();
        foreach (float side in new[] { 1f, -1f })
        {
            float s = cw.s0 - 0.4f;
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pedestrian Signal";
            pole.transform.SetParent(go.transform);
            pole.transform.position = street.PointAtOffset(s, side * (HalfRoad + 0.6f)) + Vector3.up * (CurbHeight + 1.2f);
            pole.transform.localScale = new Vector3(0.1f, 1.2f, 0.1f);
            pole.GetComponent<Renderer>().sharedMaterial = mats["SignalBody"];

            // The head faces people waiting on the opposite kerb.
            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(head.GetComponent<Collider>());
            head.name = "Signal Head";
            head.transform.SetParent(go.transform);
            head.transform.position = street.PointAtOffset(s, side * (HalfRoad + 0.6f)) + Vector3.up * (CurbHeight + 2.6f);
            head.transform.rotation = Quaternion.LookRotation(-side * street.Right(s));
            head.transform.localScale = new Vector3(0.35f, 0.65f, 0.25f);
            head.GetComponent<Renderer>().sharedMaterial = mats["SignalBody"];
            for (int i = 0; i < 2; i++)
            {
                var lamp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(lamp.GetComponent<Collider>());
                lamp.transform.SetParent(head.transform, false);
                lamp.transform.localPosition = new Vector3(0f, i == 0 ? 0.24f : -0.24f, 0.51f);
                lamp.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                lamp.transform.localScale = new Vector3(0.8f, 0.42f, 1f);
                var r = lamp.GetComponent<Renderer>();
                r.sharedMaterial = mats[i == 0 ? "LampDontWalk" : "LampWalk"];
                (i == 0 ? dontWalk : walk).Add(r);
            }
        }
        cw.walkLamps = walk.ToArray();
        cw.dontWalkLamps = dontWalk.ToArray();
        return cw;
    }

    // ------------------------------------------------------------------ pedestrians

    const string CharacterDir = "Assets/Characters";

    // One disabled template per character model. Uses rigged characters from Assets/Characters
    // (e.g. Mixamo FBX: "<Name>_Walking.fbx" per character plus one "Idle.fbx"); falls back to
    // simple stand-in figures if none are there yet.
    static GameObject[] MakePedestrianTemplates(Transform parent, Dictionary<string, Material> mats)
    {
        CharacterImport.ExtractEmbeddedTextures();
        var templates = new List<GameObject>();
        var files = Directory.Exists(CharacterDir)
            ? Directory.GetFiles(CharacterDir, "*.fbx").Select(p => p.Replace('\\', '/')).ToArray()
            : new string[0];
        var controller = files.Length > 0 ? MakeWalkController(files) : null;
        var modelFiles = files.Where(f => !Path.GetFileName(f).ToLower().Contains("idle")).ToArray();
        if (modelFiles.Length == 0) modelFiles = files;

        var rng = new System.Random(3);
        foreach (var f in modelFiles)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(f);
            if (!model) continue;
            var root = PedestrianRoot(parent);
            var vis = (GameObject)PrefabUtility.InstantiatePrefab(model);
            vis.name = "Model";
            vis.transform.SetParent(root.transform, false);
            CharacterMaterials.Apply(vis, f);
            var b = WorldBounds(vis);
            vis.transform.localScale *= (1.62f + (float)rng.NextDouble() * 0.2f) / Mathf.Max(b.size.y, 1e-3f);
            b = WorldBounds(vis);
            vis.transform.position += Vector3.up * -b.min.y;
            var anim = vis.GetComponentInChildren<Animator>();
            if (!anim) anim = vis.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            root.GetComponent<Pedestrian>().animator = anim;
            templates.Add(root);
        }

        if (templates.Count == 0)
        {
            Debug.LogWarning("SceneBuilder: no characters in Assets/Characters, using stand-in pedestrians.");
            var shirts = new[] { new Color(0.2f, 0.35f, 0.7f), new Color(0.75f, 0.2f, 0.2f), new Color(0.9f, 0.75f, 0.3f), new Color(0.25f, 0.55f, 0.3f) };
            foreach (var shirt in shirts)
            {
                var root = PedestrianRoot(parent);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.DestroyImmediate(body.GetComponent<Collider>());
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.8f, 0f);
                body.transform.localScale = new Vector3(0.45f, 0.8f, 0.3f);
                body.GetComponent<Renderer>().sharedMaterial = Mat("Shirt" + templates.Count, shirt, null, 0.2f);
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(head.GetComponent<Collider>());
                head.transform.SetParent(root.transform, false);
                head.transform.localPosition = new Vector3(0f, 1.62f, 0f);
                head.transform.localScale = Vector3.one * 0.24f;
                head.GetComponent<Renderer>().sharedMaterial = mats["Skin"];
                templates.Add(root);
            }
        }
        return templates.ToArray();
    }

    static GameObject PedestrianRoot(Transform parent)
    {
        var root = new GameObject("PedestrianTemplate");
        root.transform.SetParent(parent);
        var cap = root.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 0.9f, 0f);
        cap.height = 1.8f;
        cap.radius = 0.3f;
        root.AddComponent<Rigidbody>().isKinematic = true;
        root.AddComponent<Pedestrian>();
        root.AddComponent<VizTracked>().kind = VizTracked.Kind.Pedestrian;
        root.SetActive(false);
        return root;
    }

    // Idle <-> Walk state machine driven by the "Walking" bool. Humanoid clips retarget onto every character.
    static RuntimeAnimatorController MakeWalkController(string[] files)
    {
        AnimationClip Clip(string keyword) => files
            .Where(f => Path.GetFileName(f).ToLower().Contains(keyword))
            .SelectMany(f => AssetDatabase.LoadAllAssetsAtPath(f).OfType<AnimationClip>())
            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        var walkClip = Clip("walk");
        var idleClip = Clip("idle") ?? walkClip;
        if (!walkClip) { Debug.LogWarning("SceneBuilder: no walking clip found in Assets/Characters."); return null; }

        string path = GenDir + "/Pedestrian.controller";
        AssetDatabase.DeleteAsset(path);
        var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.AddParameter("Walking", AnimatorControllerParameterType.Bool);
        var sm = ctrl.layers[0].stateMachine;
        var idle = sm.AddState("Idle");
        idle.motion = idleClip;
        var walk = sm.AddState("Walk");
        walk.motion = walkClip;
        sm.defaultState = idle;
        var start = idle.AddTransition(walk);
        start.AddCondition(UnityEditor.Animations.AnimatorConditionMode.If, 0f, "Walking");
        start.hasExitTime = false;
        start.duration = 0.25f;
        var stop = walk.AddTransition(idle);
        stop.AddCondition(UnityEditor.Animations.AnimatorConditionMode.IfNot, 0f, "Walking");
        stop.hasExitTime = false;
        stop.duration = 0.25f;
        return ctrl;
    }

    static void MeshObject(string name, MeshBuilder mb, Material mat, Transform parent)
    {
        var mesh = mb.ToMesh(name);
        string path = $"{GenDir}/{name.Replace(' ', '_')}_{parent.name.Replace(' ', '_')}_{mesh.vertexCount}.asset";
        AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(path));
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
    }

    class MeshBuilder
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> tris = new List<int>();

        // Flat ribbon between two lateral offsets, following the road.
        public void Strip(RoadPath road, float s0, float s1, float latA, float latB, float y, float uvScale, float step)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt((s1 - s0) / step));
            int start = verts.Count;
            for (int i = 0; i <= n; i++)
            {
                float s = Mathf.Lerp(s0, s1, (float)i / n);
                Vector3 a = road.PointAtOffset(s, latA), b = road.PointAtOffset(s, latB);
                a.y = b.y = y;
                verts.Add(a); verts.Add(b);
                uvs.Add(new Vector2(0f, s / uvScale)); uvs.Add(new Vector2((latB - latA) / uvScale, s / uvScale));
                if (i > 0)
                {
                    int k = start + (i - 1) * 2;
                    tris.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                }
            }
        }

        // Vertical curb face (double-sided) at a lateral offset.
        public void Wall(RoadPath road, float s0, float s1, float lat, float y0, float y1)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(s1 - s0));
            int start = verts.Count;
            for (int i = 0; i <= n; i++)
            {
                float s = Mathf.Lerp(s0, s1, (float)i / n);
                Vector3 p = road.PointAtOffset(s, lat);
                verts.Add(new Vector3(p.x, y0, p.z)); verts.Add(new Vector3(p.x, y1, p.z));
                uvs.Add(new Vector2(s / 3f, 0f)); uvs.Add(new Vector2(s / 3f, 0.05f));
                if (i > 0)
                {
                    int k = start + (i - 1) * 2;
                    tris.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                    tris.AddRange(new[] { k, k + 1, k + 2, k + 1, k + 3, k + 2 });
                }
            }
        }

        // A straight-ahead arrow painted in a lane: shaft then head.
        public void Arrow(RoadPath road, float s, int lane, float y)
        {
            float lat = road.LaneOffset(lane);
            Strip(road, s, s + 3.2f, lat - 0.12f, lat + 0.12f, y, 1f, 0.4f);
            int i = verts.Count;
            Vector3 l = road.PointAtOffset(s + 3.2f, lat - 0.55f), r = road.PointAtOffset(s + 3.2f, lat + 0.55f), tip = road.PointAtOffset(s + 4.8f, lat);
            l.y = r.y = tip.y = y;
            verts.Add(l); verts.Add(r); verts.Add(tip);
            uvs.Add(Vector2.zero); uvs.Add(Vector2.right); uvs.Add(Vector2.up);
            tris.AddRange(new[] { i, i + 2, i + 1 });   // facing up, same winding as Strip
        }

        // A flat convex polygon (outline clockwise from above), filled as a fan from its centre.
        public void Fan(List<Vector3> outline, float uvScale = 3f)
        {
            int start = verts.Count;
            Vector3 c = Vector3.zero;
            foreach (var p in outline) c += p;
            c /= outline.Count;
            verts.Add(c);
            uvs.Add(new Vector2(c.x, c.z) / uvScale);
            foreach (var p in outline) { verts.Add(p); uvs.Add(new Vector2(p.x, p.z) / uvScale); }
            for (int i = 0; i < outline.Count; i++)
                tris.AddRange(new[] { start, start + 1 + i, start + 1 + (i + 1) % outline.Count });
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    // ------------------------------------------------------------------ materials

    static Dictionary<string, Material> CreateMaterials()
    {
        var sidewalkTex = Directory.GetFiles(TexDir, "*.jpg").Concat(Directory.GetFiles(TexDir, "*.png"))
            .Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p.Replace('\\', '/'))).FirstOrDefault(t => t);
        return new Dictionary<string, Material>
        {
            ["Asphalt"] = Mat("Asphalt", new Color(0.13f, 0.135f, 0.145f), null, 0.25f),
            ["Sidewalk"] = Mat("Sidewalk", Color.white, sidewalkTex, 0.15f),
            ["Grass"] = Mat("Grass", new Color(0.27f, 0.43f, 0.2f), null, 0.05f),
            ["Lawn"] = Mat("Lawn", new Color(0.27f, 0.43f, 0.2f), null, 0.05f),
            ["Curb"] = Mat("Curb", new Color(0.62f, 0.62f, 0.6f), null, 0.15f),
            ["Marking"] = Mat("Marking", new Color(0.95f, 0.95f, 0.92f), null, 0.3f),
            ["LampRed"] = Mat("LampRed", new Color(0.3f, 0.02f, 0.02f), null, 0.8f, true),
            ["LampYellow"] = Mat("LampYellow", new Color(0.3f, 0.2f, 0.02f), null, 0.8f, true),
            ["LampGreen"] = Mat("LampGreen", new Color(0.02f, 0.3f, 0.08f), null, 0.8f, true),
            ["LampWalk"] = Mat("LampWalk", new Color(0.1f, 0.12f, 0.13f), null, 0.6f, true),
            ["LampDontWalk"] = Mat("LampDontWalk", new Color(0.13f, 0.08f, 0.04f), null, 0.6f, true),
            ["SignalBody"] = Mat("SignalBody", new Color(0.12f, 0.12f, 0.12f), null, 0.4f),
            ["Skin"] = Mat("Skin", new Color(0.85f, 0.66f, 0.52f), null, 0.3f),
        };
    }

    static Material Mat(string name, Color color, Texture2D tex, float smoothness, bool emissive = false)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var m = new Material(shader) { name = name };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (tex)
        {
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        if (emissive)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", Color.black);
        }
        string path = $"{GenDir}/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }
}
