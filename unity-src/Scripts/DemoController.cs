using UnityEngine;
using UnityEngine.SceneManagement;

// Keyboard controls and the on-screen dashboard for the class demo.
public class DemoController : MonoBehaviour
{
    public AutoPilot robotaxi;
    public CameraRig cameraRig;
    public RoadPath road;
    public TeslaViz viz;
    public GameObject[] obstacleTemplates;
    public GameObject[] pedestrianTemplates;

    public bool FullViz { get; set; }

    bool showHelp = true, paused;
    float dropUntil;
    CarController car;
    SensorArray sensors;
    GUIStyle panel, title, heading, speed, body, help, vizSpeed, vizUnit, vizGear, vizGearOn, vizPill, vizStatus;
    Texture2D bg, pillOn, pillOff;

    void Start()
    {
        car = robotaxi.GetComponent<CarController>();
        sensors = robotaxi.GetComponent<SensorArray>();
        DynamicGI.UpdateEnvironment();   // ambient light from the HDR sky
    }

    void Update()
    {
        if (KeyInput.Down(KeyCode.M)) car.autopilotEngaged = !car.autopilotEngaged;
        if (KeyInput.Down(KeyCode.C)) cameraRig.Next();
        if (KeyInput.Down(KeyCode.B)) dropUntil = Time.time + 15f;   // drops at the next safe spot ahead
        if (Time.time < dropUntil && DropObstacle()) dropUntil = 0f;
        if (KeyInput.Down(KeyCode.P)) SpawnJaywalker();
        if (KeyInput.Down(KeyCode.V)) FullViz = !FullViz;
        if (KeyInput.Down(KeyCode.T)) sensors.showRays = !sensors.showRays;
        if (KeyInput.Down(KeyCode.H)) showHelp = !showHelp;
        if (KeyInput.Down(KeyCode.Space)) { paused = !paused; Time.timeScale = paused ? 0f : 1f; }
        if (KeyInput.Down(KeyCode.R)) { Time.timeScale = 1f; SceneManager.LoadScene(SceneManager.GetActiveScene().name); }
    }

    // Drops a random obstacle (barrier, barrel, box) in the robotaxi's lane, 35 m or more ahead,
    // on a free stretch: never on top of something, right in front of another car (it could not
    // stop in time), on a zebra crossing, or beside something already blocking the other lane.
    public bool DropObstacle()
    {
        if (obstacleTemplates == null || obstacleTemplates.Length == 0) return false;
        int lane = robotaxi.TargetLane;
        for (float ahead = 35f; ahead <= 90f; ahead += 5f)
        {
            float s = robotaxi.S + ahead;
            if (robotaxi.NearCrossing(s, 10f, 25f) || NearJunction(s) || Occupied(s, lane, 5f, false) || Occupied(s - 16f, lane, 14f, false)
                || Occupied(s, 1 - lane, 35f, true)) continue;   // room for long vehicles to weave between
            var template = obstacleTemplates[Random.Range(0, obstacleTemplates.Length)];
            var o = Instantiate(template, road.Point(s, lane), Quaternion.LookRotation(road.Tangent(s)) * Quaternion.Euler(0f, Random.Range(-15f, 15f), 0f));
            o.name = template.name.Replace(" (template)", " (dropped)");
            Debug.Log($"[DROP] {o.name} lane {lane} at s={road.Wrap(s):0} {o.transform.position}");
            o.SetActive(true);
            return true;
        }
        return false;   // no free stretch ahead right now
    }

    // On or near a turn or a signalised stop line (junctions are no place for a dropped obstacle).
    bool NearJunction(float s)
    {
        // (long vehicles need straight road behind the obstacle to stop on and pull out from)
        if (road.MaxCurvature(s - 35f, s + 15f) > 0.03f) return true;
        Vector3 p = road.Center(s);
        foreach (var tl in TrafficLight.All) if (Vector3.Distance(p, tl.stopPoint) < 25f) return true;
        // Every approach to the spot needs straight road: a long vehicle stops ~15 m back and pulls
        // out from there, so keep well clear of the junction or corner behind, and leave room to get
        // back into the right lane before the one ahead (more before a split junction).
        Vector3 t = road.Tangent(s);
        foreach (var j in Junction.All)
        {
            Vector3 to = p - j.transform.position;
            to.y = 0f;
            float need = Vector3.Dot(to, t) > 0f ? 38f : j.split ? 46f : 34f;
            if (to.magnitude < need) return true;
        }
        return false;
    }

    // Anything in `lane` within +-halfLength of s? With staticOnly, moving vehicles and people are ignored.
    bool Occupied(float s, int lane, float halfLength, bool staticOnly)
    {
        var hits = Physics.OverlapBox(road.Point(s, lane) + Vector3.up, new Vector3(1.6f, 0.9f, halfLength),
            Quaternion.LookRotation(road.Tangent(s)), ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            bool moving = (h.attachedRigidbody && h.attachedRigidbody.GetComponent<CarController>()) || h.GetComponentInParent<Pedestrian>();
            if (!staticOnly || !moving) return true;
        }
        return false;
    }

    // A person steps off the right-hand sidewalk 30 m ahead of the robotaxi and crosses.
    public void SpawnJaywalker()
    {
        if (pedestrianTemplates == null || pedestrianTemplates.Length == 0) return;
        var template = pedestrianTemplates[Random.Range(0, pedestrianTemplates.Length)];
        var p = Instantiate(template);
        p.name = "Jaywalker";
        float s = robotaxi.S + 30f;
        for (float ahead = 30f; ahead <= 60f && NearJunction(robotaxi.S + ahead); ahead += 5f) s = robotaxi.S + ahead + 5f;
        p.GetComponent<Pedestrian>().StartJaywalk(road, s, 1f);
        p.SetActive(true);
    }

    void OnGUI()
    {
        EnsureStyles();
        float scale = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float w = Screen.width / scale, h = 1080f;

        if (FullViz)
        {
            DrawViz(new Rect(0, 0, w, h), true);
            return;
        }

        GUI.Label(new Rect(0, 18, w, 50), "AUTONOMOUS ROBOTAXI SIMULATION", title);

        GUILayout.BeginArea(new Rect(24, 80, 470, 420), panel);
        GUILayout.Label(car.autopilotEngaged ? "<color=#3ff5c8>● AUTOPILOT ENGAGED</color>" : "<color=#ffb347>● MANUAL CONTROL</color>", heading);
        GUILayout.Label($"{Mathf.Abs(car.Speed) * 3.6f:0} <size=24>km/h</size>", speed);
        GUILayout.Label($"<b>Status</b>   {robotaxi.Status}", body);
        GUILayout.Label($"<b>Target speed</b>   {robotaxi.DesiredSpeed * 3.6f:0} km/h", body);
        GUILayout.Label($"<b>Lane</b>   {(robotaxi.Lane < 0.5f ? "right" : "left")}", body);
        string obstacle = float.IsInfinity(robotaxi.ObstacleDistance) ? "clear"
            : robotaxi.ObstacleIsPedestrian ? $"<color=#ffb347>PEDESTRIAN</color> {robotaxi.ObstacleDistance:0.0} m ahead"
            : $"{robotaxi.ObstacleDistance:0.0} m ahead" + (robotaxi.ObstacleSpeed > 0.5f ? $", moving {robotaxi.ObstacleSpeed * 3.6f:0} km/h" : ", stationary");
        GUILayout.Label($"<b>Obstacle in lane</b>   {obstacle}", body);
        string light = robotaxi.NextLight
            ? $"{LightColor(robotaxi.NextLight.State)} in {robotaxi.NextLightDistance:0} m  ({robotaxi.NextLight.TimeLeft:0}s left)"
            : "none nearby";
        GUILayout.Label($"<b>Traffic light (V2I)</b>   {light}", body);
        if (robotaxi.YieldingTo)
            GUILayout.Label("<b>Crosswalk</b>   <color=#ffb347>pedestrians crossing: yielding</color>", body);
        GUILayout.Label($"<b>Steering</b>   {car.SteerDeg:+0;-0;0}°     <b>Accel</b>   {car.Accel:+0.0;-0.0;0.0} m/s²", body);
        GUILayout.EndArea();

        DrawViz(new Rect(w - 664, h - 444, 640, 420), false);

        if (showHelp)
        {
            GUILayout.BeginArea(new Rect(24, h - 290, 470, 266), panel);
            GUILayout.Label("<b>Controls</b>", body);
            GUILayout.Label("P  a pedestrian jaywalks in front of the car\nB  drop an obstacle in front of the car\nV  full-screen autopilot view\nM  toggle autopilot / manual (WASD)\nC  change camera     T  show/hide sensor rays\nSpace  pause     R  restart     H  hide this help", help);
            GUILayout.EndArea();
        }
    }

    // The autopilot visualisation with a Tesla-style overlay: gear, speed, autopilot state, status.
    void DrawViz(Rect r, bool full)
    {
        if (!viz || !viz.Texture) return;
        GUI.DrawTexture(r, viz.Texture, ScaleMode.ScaleAndCrop, false);
        if (!full)
        {
            var edge = new Color(1f, 1f, 1f, 0.12f);
            var prev = GUI.color;
            GUI.color = edge;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        float k = full ? 1.5f : 1f;
        float x = r.x + 22 * k, y = r.y + 16 * k;
        GUI.Label(new Rect(x, y, 200 * k, 30 * k), car.Speed < -0.1f ? "P  <b><color=#ffffff>R</color></b>  N  D" : "P  R  N  <b><color=#ffffff>D</color></b>", full ? vizGearOn : vizGear);
        GUI.Label(new Rect(x, y + 26 * k, 200 * k, 80 * k), $"{Mathf.Abs(car.Speed) * 3.6f:0}", vizSpeed);
        GUI.Label(new Rect(x + 4 * k, y + 92 * k, 120 * k, 26 * k), "KM/H", vizUnit);

        bool ap = car.autopilotEngaged;
        var pill = new Rect(r.xMax - 190 * k, y + 4 * k, 168 * k, 32 * k);
        GUI.Box(pill, GUIContent.none, new GUIStyle { normal = { background = ap ? pillOn : pillOff } });
        GUI.Label(pill, ap ? "◉  AUTOPILOT" : "○  MANUAL", vizPill);

        if (robotaxi.NextLight)
            GUI.Label(new Rect(r.xMax - 190 * k, y + 44 * k, 168 * k, 28 * k),
                $"{LightColor(robotaxi.NextLight.State)}  {robotaxi.NextLightDistance:0} m", vizStatus);

        GUI.Label(new Rect(r.x, r.yMax - 46 * k, r.width, 34 * k), robotaxi.Status, vizStatus);
    }

    static string LightColor(TrafficLight.Phase p) =>
        p == TrafficLight.Phase.Red ? "<color=#ff4a3d>RED</color>" :
        p == TrafficLight.Phase.Yellow ? "<color=#ffc234>YELLOW</color>" : "<color=#3dff7a>GREEN</color>";

    static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    void EnsureStyles()
    {
        if (panel != null) return;
        bg = Solid(new Color(0.05f, 0.07f, 0.1f, 0.78f));
        pillOn = Solid(new Color(0.16f, 0.45f, 1f, 0.95f));
        pillOff = Solid(new Color(0.3f, 0.32f, 0.36f, 0.9f));
        panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(20, 20, 16, 16) };
        panel.normal.background = bg;
        title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
        title.normal.textColor = Color.white;
        heading = new GUIStyle(title) { fontSize = 22, alignment = TextAnchor.MiddleLeft };
        speed = new GUIStyle(title) { fontSize = 64, alignment = TextAnchor.MiddleLeft };
        body = new GUIStyle(GUI.skin.label) { fontSize = 20, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = true };
        body.normal.textColor = new Color(0.9f, 0.93f, 0.97f);
        help = new GUIStyle(body) { fontSize = 18 };
        help.normal.textColor = new Color(0.75f, 0.8f, 0.86f);

        vizGear = new GUIStyle(GUI.skin.label) { fontSize = 18, richText = true };
        vizGear.normal.textColor = new Color(0.55f, 0.58f, 0.63f);
        vizGearOn = new GUIStyle(vizGear) { fontSize = 26 };
        vizSpeed = new GUIStyle(GUI.skin.label) { fontSize = 60, fontStyle = FontStyle.Bold };
        vizSpeed.normal.textColor = Color.white;
        vizUnit = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        vizUnit.normal.textColor = new Color(0.6f, 0.63f, 0.68f);
        vizPill = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        vizPill.normal.textColor = Color.white;
        vizStatus = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, richText = true };
        vizStatus.normal.textColor = new Color(0.85f, 0.88f, 0.93f);
    }
}
