using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tesla-style driving visualisation. A second camera renders only layer 30, where every
// tracked vehicle, person and obstacle has a "clay" twin (same meshes, same skeletons, so wheels
// spin and people walk), over a dark road with bright lane lines. It adds the planned path,
// a stop bar where the car intends to stop, traffic-light state and the live lidar returns.
// The result goes into a RenderTexture that the dashboard draws.
public class TeslaViz : MonoBehaviour
{
    public const int Layer = 30;
    public AutoPilot ego;
    public RoadPath road;
    public Shader litShader, unlitShader;   // set by the scene builder so both ship in the build
    public RenderTexture Texture { get; private set; }

    Camera cam;
    SensorArray sensors;
    Material clay, egoMat, leadMat, personMat, obstacleMat, darkMat, roadMat, walkMat, lineMat, groundMat, pathMat, pointMat;

    class Proxy { public VizTracked src; public Collider col; public List<Renderer> renderers = new List<Renderer>(); public Material baseMat; public bool lead; }
    readonly Dictionary<VizTracked, Proxy> proxies = new Dictionary<VizTracked, Proxy>();
    readonly List<(TrafficLight tl, Renderer red, Renderer yellow, Renderer green)> lights = new List<(TrafficLight, Renderer, Renderer, Renderer)>();
    readonly Material[] lampOn = new Material[3];

    Mesh pathMesh;
    readonly List<Transform> points = new List<Transform>();
    float scanTimer;
    bool savedFog;
    Vector3 camVel;

    void Start()
    {
        // Hide the twins from the main view before anything else can go wrong.
        if (Camera.main) Camera.main.cullingMask &= ~(1 << Layer);
        sensors = ego.GetComponent<SensorArray>();
        CreateMaterials();
        BuildRoad();
        BuildCamera();
        BuildPath();
        BuildLights();
        BuildPoints();
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        if (Texture) Texture.Release();
    }

    // The city's light-blue fog would wash out the dark view, so switch it off for this camera only.
    void OnBeginCamera(ScriptableRenderContext _, Camera c) { if (c == cam) { savedFog = RenderSettings.fog; RenderSettings.fog = false; } }
    void OnEndCamera(ScriptableRenderContext _, Camera c) { if (c == cam) RenderSettings.fog = savedFog; }

    // ------------------------------------------------------------------ setup

    Material Lit(Color c, float smooth)
    {
        var m = new Material(litShader);
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smooth);
        return m;
    }

    Material Unlit(Color c)
    {
        var m = new Material(unlitShader);
        m.SetColor("_BaseColor", c);
        return m;
    }

    void CreateMaterials()
    {
        clay = Lit(new Color(0.62f, 0.65f, 0.7f), 0.55f);
        egoMat = Lit(new Color(0.93f, 0.94f, 0.96f), 0.65f);
        leadMat = Lit(new Color(0.25f, 0.55f, 1f), 0.6f);
        personMat = Lit(new Color(0.95f, 0.95f, 0.95f), 0.3f);
        obstacleMat = Lit(new Color(1f, 0.55f, 0.2f), 0.4f);
        darkMat = Lit(new Color(0.18f, 0.19f, 0.21f), 0.4f);
        roadMat = Lit(new Color(0.12f, 0.13f, 0.15f), 0.2f);
        walkMat = Lit(new Color(0.2f, 0.215f, 0.24f), 0.1f);
        groundMat = Lit(new Color(0.075f, 0.08f, 0.09f), 0f);
        lineMat = Unlit(new Color(0.78f, 0.82f, 0.88f));
        pointMat = Unlit(new Color(0.35f, 0.9f, 1f));
        lampOn[0] = Unlit(new Color(1f, 0.2f, 0.15f));
        lampOn[1] = Unlit(new Color(1f, 0.75f, 0.1f));
        lampOn[2] = Unlit(new Color(0.2f, 1f, 0.45f));
        pathMat = new Material(Shader.Find("Sprites/Default"));
    }

    static GameObject Twin(string name, Mesh mesh, Material mat, Transform like)
    {
        var go = new GameObject(name) { layer = Layer };
        go.transform.SetPositionAndRotation(like.position, like.rotation);
        go.transform.localScale = like.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        return go;
    }

    void BuildRoad()
    {
        var root = new GameObject("Viz World") { layer = Layer }.transform;
        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            Material m = mf.name switch
            {
                "Asphalt" => roadMat,
                "Sidewalks" => walkMat,
                "Lane Markings" or "Zebra Stripes" or "Stop Line" => lineMat,
                _ => null,
            };
            if (m == null || mf.gameObject.layer == Layer) continue;
            Twin(mf.name, mf.sharedMesh, m, mf.transform).transform.SetParent(root, true);
        }
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Destroy(ground.GetComponent<Collider>());
        ground.layer = Layer;
        ground.transform.SetParent(root);
        ground.transform.localScale = new Vector3(60f, 1f, 60f);
        ground.transform.position = new Vector3(0f, -0.01f, 0f);
        ground.GetComponent<Renderer>().sharedMaterial = groundMat;
    }

    void BuildCamera()
    {
        Texture = new RenderTexture(1280, 800, 24) { antiAliasing = 4, name = "Viz" };
        var go = new GameObject("Viz Camera");
        cam = go.AddComponent<Camera>();
        cam.cullingMask = 1 << Layer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.065f, 0.075f);
        cam.fieldOfView = 48f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 260f;
        cam.targetTexture = Texture;
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderShadows = false;
        data.renderPostProcessing = false;
        go.transform.position = ego.transform.position - ego.transform.forward * 10f + Vector3.up * 6f;
    }

    void BuildPath()
    {
        pathMesh = new Mesh { name = "Planned Path" };
        pathMesh.MarkDynamic();
        var go = new GameObject("Viz Path") { layer = Layer };
        go.AddComponent<MeshFilter>().sharedMesh = pathMesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = pathMat;
    }

    void BuildLights()
    {
        foreach (var tl in FindObjectsByType<TrafficLight>(FindObjectsSortMode.None))
        {
            Renderer red = null, yellow = null, green = null;
            foreach (var r in tl.GetComponentsInChildren<MeshRenderer>())
            {
                var twin = AddTwin(r, darkMat);
                if (r == tl.redLamp) red = twin;
                if (r == tl.yellowLamp) yellow = twin;
                if (r == tl.greenLamp) green = twin;
            }
            lights.Add((tl, red, yellow, green));
        }
    }

    void BuildPoints()
    {
        var root = new GameObject("Viz Lidar") { layer = Layer }.transform;
        for (int i = 0; i < sensors.Rays.Count; i++)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(p.GetComponent<Collider>());
            p.layer = Layer;
            p.transform.SetParent(root);
            p.transform.localScale = Vector3.one * 0.14f;
            var r = p.GetComponent<Renderer>();
            r.sharedMaterial = pointMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            points.Add(p.transform);
        }
    }

    // A clay copy of one renderer, parented under it so it follows every movement (wheels, bones).
    static Renderer AddTwin(Renderer src, Material mat)
    {
        var go = new GameObject("VizTwin") { layer = Layer };
        go.transform.SetParent(src.transform, false);
        Renderer r;
        if (src is SkinnedMeshRenderer smr)
        {
            var s = go.AddComponent<SkinnedMeshRenderer>();
            s.sharedMesh = smr.sharedMesh;
            s.bones = smr.bones;
            s.rootBone = smr.rootBone;
            s.updateWhenOffscreen = true;
            r = s;
        }
        else
        {
            var mf = src.GetComponent<MeshFilter>();
            if (!mf) { Destroy(go); return null; }
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            r = go.AddComponent<MeshRenderer>();
        }
        var mats = new Material[Mathf.Max(1, src.sharedMaterials.Length)];
        for (int i = 0; i < mats.Length; i++) mats[i] = mat;
        r.sharedMaterials = mats;
        r.shadowCastingMode = ShadowCastingMode.Off;
        return r;
    }

    Material MaterialFor(VizTracked.Kind k) => k switch
    {
        VizTracked.Kind.Ego => egoMat,
        VizTracked.Kind.Pedestrian => personMat,
        VizTracked.Kind.Obstacle => obstacleMat,
        _ => clay,
    };

    void Scan()
    {
        foreach (var t in FindObjectsByType<VizTracked>(FindObjectsSortMode.None))
        {
            if (proxies.ContainsKey(t)) continue;
            var p = new Proxy { src = t, col = t.GetComponent<Collider>(), baseMat = MaterialFor(t.kind) };
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (r.gameObject.layer == Layer || r is LineRenderer) continue;
                var twin = AddTwin(r, p.baseMat);
                if (twin) p.renderers.Add(twin);
            }
            proxies[t] = p;
        }
        var dead = new List<VizTracked>();
        foreach (var kv in proxies) if (!kv.Key) dead.Add(kv.Key);
        foreach (var d in dead) proxies.Remove(d);
    }

    // ------------------------------------------------------------------ per frame

    void LateUpdate()
    {
        scanTimer -= Time.unscaledDeltaTime;
        if (scanTimer <= 0f) { Scan(); scanTimer = 0.3f; }

        // Highlight whatever the car is currently reacting to.
        foreach (var p in proxies.Values)
        {
            bool lead = p.col && p.col == ego.ObstacleCollider;
            if (lead == p.lead) continue;
            p.lead = lead;
            foreach (var r in p.renderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = lead ? leadMat : p.baseMat;
                r.sharedMaterials = mats;
            }
        }

        foreach (var (tl, red, yellow, green) in lights)
        {
            if (red) red.sharedMaterial = tl.State == TrafficLight.Phase.Red ? lampOn[0] : darkMat;
            if (yellow) yellow.sharedMaterial = tl.State == TrafficLight.Phase.Yellow ? lampOn[1] : darkMat;
            if (green) green.sharedMaterial = tl.State == TrafficLight.Phase.Green ? lampOn[2] : darkMat;
        }

        for (int i = 0; i < points.Count; i++)
        {
            var ray = sensors.Rays[i];
            points[i].gameObject.SetActive(ray.hit);
            if (ray.hit) points[i].position = ray.point;
        }

        UpdatePath();

        Vector3 fwd = ego.transform.forward;
        fwd.y = 0f;
        fwd.Normalize();
        Vector3 want = ego.transform.position - fwd * 9f + Vector3.up * 5.2f;
        cam.transform.position = Vector3.SmoothDamp(cam.transform.position, want, ref camVel, 0.25f);
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation,
            Quaternion.LookRotation(ego.transform.position + fwd * 11f - cam.transform.position), 8f * Time.deltaTime);
    }

    // Blue ribbon along the planned path (it bends through a lane change); it ends in a white stop
    // bar when the car plans to stop. While waiting for a gap, a grey ribbon shows the path it wants.
    void UpdatePath()
    {
        const float maxLen = 45f, halfW = 0.8f;
        float start = 2f;
        bool stopping = !float.IsInfinity(ego.StopDistance) && ego.StopDistance < maxLen;
        float len = stopping ? Mathf.Clamp(ego.StopDistance + 2f, start + 0.5f, maxLen) : maxLen;
        var verts = new List<Vector3>();
        var cols = new List<Color>();
        var tris = new List<int>();

        void Ribbon(float from, float to, System.Func<float, float> laneAt, Color c, bool fade)
        {
            int steps = 30, b = verts.Count;
            for (int i = 0; i <= steps; i++)
            {
                float x = Mathf.Lerp(from, to, (float)i / steps);
                float s = ego.S + x;
                Vector3 p = road.Point(s, laneAt(s)) + Vector3.up * 0.06f;
                Vector3 right = road.Right(s);
                verts.Add(p - right * halfW);
                verts.Add(p + right * halfW);
                float a = fade ? Mathf.Lerp(c.a, 0f, (float)i / steps) : c.a;
                cols.Add(new Color(c.r, c.g, c.b, a));
                cols.Add(new Color(c.r, c.g, c.b, a));
                if (i > 0)
                {
                    int k = b + (i - 1) * 2;
                    tris.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                }
            }
        }

        Ribbon(start, len, ego.LaneAt, new Color(0.2f, 0.55f, 1f, 0.8f), !stopping);
        if (stopping)
        {
            float s = ego.S + len;
            Vector3 c = road.Point(s, ego.LaneAt(s)) + Vector3.up * 0.07f;
            Vector3 right = road.Right(s), f = road.Tangent(s);
            int b = verts.Count;
            verts.Add(c - right * 1.5f); verts.Add(c + right * 1.5f);
            verts.Add(c - right * 1.5f + f * 0.45f); verts.Add(c + right * 1.5f + f * 0.45f);
            for (int i = 0; i < 4; i++) cols.Add(new Color(1f, 1f, 1f, 0.95f));
            tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        if (ego.PendingLane >= 0)
        {
            float from = ego.Lane, to = ego.PendingLane;
            Ribbon(start, 26f, s => Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((s - ego.S - start) / 14f))),
                new Color(0.75f, 0.78f, 0.85f, 0.35f), true);
        }
        pathMesh.Clear();
        pathMesh.SetVertices(verts);
        pathMesh.SetColors(cols);
        pathMesh.SetTriangles(tris, 0);
        pathMesh.RecalculateBounds();
    }
}
