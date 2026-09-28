using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// Automated traffic test. Run the game with  -simtest <folder>  (add -batchmode -nographics to skip
// rendering). It simulates 6 minutes of traffic at 4x speed, drops an obstacle in front of the
// robotaxi every 45 s and sends a jaywalker every 60 s, and checks every vehicle for:
// touching anything (vehicle, obstacle, person), facing against traffic, leaving the road, and
// standing still too long. Writes simtest.txt with PASS/FAIL, then quits. Does nothing otherwise.
public class SimTest : MonoBehaviour
{
    const float Duration = 360f, TimeScale = 4f, MaxStop = 30f;
    string dir;

    class Track
    {
        public AutoPilot pilot;
        public CarController car;
        public BoxCollider box;
        public float distance, stopped, longestStop, wrongWay, offRoad, trackError, backwards, onCrossing, leftAtSplit;
        public int redRuns;
        public TrafficLight lastLight;
        public float lastLightDistance;
        public string longestStopStatus = "";
        public int laneChanges;
        public bool wasChanging;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-simtest");
        if (i < 0 || i + 1 >= args.Length) return;
        new GameObject("SimTest").AddComponent<SimTest>().dir = args[i + 1];
    }

    static bool OnCrossing(Crosswalk cw, Collider body)
    {
        foreach (var c in Physics.OverlapBox(cw.Center + Vector3.up, new Vector3(4f, 0.5f, (cw.s1 - cw.s0) * 0.5f), Quaternion.LookRotation(cw.Direction), ~0, QueryTriggerInteraction.Ignore))
            if (c == body) return true;
        return false;
    }

    static string NameOf(Collider c)
    {
        var t = c.attachedRigidbody ? c.attachedRigidbody.transform : c.transform;
        return t.name;
    }

    IEnumerator Start()
    {
        Directory.CreateDirectory(dir);
        Time.timeScale = TimeScale;
        var demo = FindAnyObjectByType<DemoController>();
        var tracks = FindObjectsByType<AutoPilot>(FindObjectsSortMode.None)
            .Select(p => new Track { pilot = p, car = p.GetComponent<CarController>(), box = p.GetComponent<BoxCollider>() }).ToList();
        var events = new StringBuilder();
        var contacts = new HashSet<string>();
        float nextDrop = 30f, nextJaywalker = 50f, nextLog = 0f;
        var wait = new WaitForFixedUpdate();

        while (Time.time < Duration)
        {
            yield return wait;
            float dt = Time.fixedDeltaTime;
            if (Time.time >= nextDrop)
            {
                // try every second until there is a free stretch, then wait 45 s
                if (demo.DropObstacle()) { nextDrop = Time.time + 45f; events.AppendLine($"t={Time.time:0} dropped an obstacle ahead of the robotaxi"); }
                else nextDrop = Time.time + 1f;
            }
            if (Time.time >= nextJaywalker) { demo.SpawnJaywalker(); nextJaywalker += 60f; events.AppendLine($"t={Time.time:0} jaywalker ahead of the robotaxi"); }

            foreach (var t in tracks)
            {
                var tr = t.pilot.transform;
                t.distance += t.car.Speed * dt;
                if (Mathf.Abs(t.car.Speed) < 0.3f)
                {
                    t.stopped += dt;
                    if (t.stopped > t.longestStop) { t.longestStop = t.stopped; t.longestStopStatus = t.pilot.Status; }
                }
                else t.stopped = 0f;
                if (t.stopped > 20f && (int)(t.stopped / 20f) != (int)((t.stopped - dt) / 20f))
                    events.AppendLine($"t={Time.time:0} LONG STOP {tr.name}: {t.pilot.Status}; lane {t.pilot.Lane:0.0} s={t.pilot.S:0} obst {t.pilot.ObstacleName} @{t.pilot.ObstacleDistance:0.0}; gap blocker {t.pilot.GapBlocker}; lane logic: {t.pilot.LaneWhy}; changing {t.pilot.ChangingLanes} pending {t.pilot.PendingLane}");

                var route = t.pilot.road;
                float s = route.Project(tr.position, t.pilot.S);
                if (Vector3.Dot(tr.forward, route.Tangent(s)) < 0.5f) t.wrongWay += dt;
                float lateral = route.Lateral(tr.position, s);
                if (Mathf.Abs(lateral) > 4.3f) t.offRoad += dt;
                if (t.car.Speed < -0.05f) t.backwards += dt;
                t.trackError = Mathf.Max(t.trackError, Mathf.Abs(lateral - route.LaneOffset(t.pilot.LaneAt(s))));
                if (t.pilot.ChangingLanes && !t.wasChanging) t.laneChanges++;
                // Ran a red light: the bumper went over a stop line that had already been red for a second
                // (crossing on the amber-to-red change is entering on amber).
                if (t.lastLight && t.lastLight.State == TrafficLight.Phase.Red && t.lastLight.redTime - t.lastLight.TimeLeft > 1f
                    && t.lastLightDistance < 1.5f && t.pilot.NextLight != t.lastLight)
                {
                    t.redRuns++;
                    events.AppendLine($"t={Time.time:0.0} RED LIGHT RUN by {tr.name} (status: {t.pilot.Status}, v={t.car.Speed * 3.6f:0} km/h, last d={t.lastLightDistance:0.00}, light left {t.lastLight.TimeLeft:0.0}s)");
                }
                t.lastLight = t.pilot.NextLight;
                t.lastLightDistance = t.pilot.NextLightDistance;
                foreach (var j in Junction.All)
                    if (j.split && Vector3.Distance(tr.position, j.transform.position) < j.radius - 2f && t.pilot.Lane > 0.5f)
                    {
                        if (contacts.Add($"split {tr.name} {(int)(Time.time / 5f)}"))
                            events.AppendLine($"t={Time.time:0.0} LEFT LANE AT SPLIT {j.name}: {tr.name} lane {t.pilot.Lane:0.00} status {t.pilot.Status} v={t.car.Speed * 3.6f:0} changing={t.pilot.ChangingLanes}");
                        t.leftAtSplit += dt;
                    }
                // Standing on a zebra crossing.
                if (Mathf.Abs(t.car.Speed) < 0.3f)
                    foreach (var cw in Crosswalk.All)
                        if (cw.road && OnCrossing(cw, t.box))
                        { t.onCrossing += dt; break; }
                t.wasChanging = t.pilot.ChangingLanes;

                // Touching anything solid? (slightly shrunk box so grazing faces don't count)
                var hits = Physics.OverlapBox(tr.TransformPoint(t.box.center), t.box.size * 0.5f * 0.95f, tr.rotation, ~0, QueryTriggerInteraction.Ignore);
                foreach (var c in hits)
                {
                    if (c.transform.IsChildOf(tr)) continue;
                    string key = $"{tr.name} x {NameOf(c)}";
                    if (contacts.Add(key)) events.AppendLine($"t={Time.time:0.0} CONTACT {key} (status: {t.pilot.Status}, lane {t.pilot.Lane:0.00}, {t.car.Speed * 3.6f:0} km/h, s={t.pilot.S:0})");
                }
            }

            if (Time.time >= nextLog)
            {
                nextLog += 30f;
                Debug.Log($"[SIMTEST] t={Time.time:0} " + string.Join(" | ", tracks.Select(t => $"{t.pilot.name.Split(' ')[0]} {t.car.Speed * 3.6f:0}km/h {t.pilot.Status}")));
            }
        }

        var report = new StringBuilder();
        bool pass = contacts.All(c => c.StartsWith("split "));
        report.AppendLine($"Traffic simulation test: {Duration:0} s simulated, {tracks.Count} vehicles on their own routes");
        report.AppendLine();
        report.AppendLine($"{"vehicle",-30}{"laps",6}{"lane chg",10}{"longest stop",14}  {"wrong-way s",12}{"off-road s",11}{"max track err",15}{"reverse s",10}{"red runs",9}{"on zebra s",11}{"L@split s",10}  stop reason");
        foreach (var t in tracks)
        {
            bool ok = t.wrongWay < 0.1f && t.offRoad < 0.1f && t.longestStop < MaxStop && t.backwards < 0.05f && t.redRuns == 0 && t.leftAtSplit < 0.05f;
            pass &= ok;
            report.AppendLine($"{t.pilot.name,-30}{t.distance / t.pilot.road.Length,6:0.0}{t.laneChanges,10}{t.longestStop,13:0.0}s  {t.wrongWay,12:0.0}{t.offRoad,11:0.0}{t.trackError,13:0.00} m{t.backwards,10:0.0}{t.redRuns,9}{t.onCrossing,11:0.0}{t.leftAtSplit,10:0.0}  {t.longestStopStatus}{(ok ? "" : "   <-- FAIL")}");
        }
        report.AppendLine();
        int touching = contacts.Count(c => !c.StartsWith("split "));
        report.AppendLine($"Contacts: {touching}");
        report.AppendLine("Events:");
        report.Append(events);
        report.AppendLine();
        report.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
        File.WriteAllText(Path.Combine(dir, "simtest.txt"), report.ToString());
        Debug.Log("[SIMTEST] " + (pass ? "PASS" : "FAIL") + "\n" + report);
        Application.Quit();
    }
}
