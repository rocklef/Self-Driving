using System.Collections;
using System.IO;
using UnityEngine;

// Test harness: run the game with  -autocapture <folder>  and it logs what the cars and people are
// doing every second, saves screenshots (every camera, the autopilot view, close-ups of a person
// and a traffic car), spawns a jaywalker, then quits. Does nothing otherwise.
public class AutoCapture : MonoBehaviour
{
    string dir;
    CameraRig rig;
    DemoController demo;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-autocapture");
        if (i < 0 || i + 1 >= args.Length) return;
        var go = new GameObject("AutoCapture");
        go.AddComponent<AutoCapture>().dir = args[i + 1];
    }

    IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
        yield return null;
        yield return null;
    }

    IEnumerator CloseUp(Transform target, string name, float distance)
    {
        rig.enabled = false;
        var cam = rig.transform;
        cam.position = target.position + target.forward * distance + target.right * distance * 0.45f + Vector3.up * 1.5f;
        cam.LookAt(target.position + Vector3.up * 0.9f);
        yield return null;
        yield return Shot(name);
        rig.enabled = true;
    }

    IEnumerator Start()
    {
        Directory.CreateDirectory(dir);
        rig = FindAnyObjectByType<CameraRig>();
        demo = FindAnyObjectByType<DemoController>();
        var pilots = FindObjectsByType<AutoPilot>(FindObjectsSortMode.None);
        var ego = demo.robotaxi;
        var shotAreas = new System.Collections.Generic.HashSet<string>();
        foreach (var v in FindObjectsByType<VizTracked>(FindObjectsSortMode.None))
            if (v.kind is VizTracked.Kind.Vehicle or VizTracked.Kind.Truck or VizTracked.Kind.Ego && v.TryGetComponent(out BoxCollider bc))
                Debug.Log($"[AUTO] vehicle {v.name}: size {bc.size} at {v.transform.position}");
        var plan = new (float t, string what)[]
        {
            (1.5f, "overview"), (3f, "chase"), (8f, "viz"), (12f, "orbit"), (15f, "person"), (18f, "car"), (22f, "top"),
            (27f, "chase"), (32f, "viz"), (36f, "person"), (40f, "driver"), (46f, "chase"), (47.5f, "viz"),
            (55f, "chase"), (62f, "viz"), (70f, "top"),
        };
        int next = 0;
        float lastLog = -1f;
        bool jaywalked = false;
        while (Time.time < 75f)
        {
            if (!jaywalked && Time.time >= 44f)
            {
                jaywalked = true;
                demo.SpawnJaywalker();
                Debug.Log("[AUTO] spawned jaywalker");
            }
            if (Time.time - lastLog >= 1f)
            {
                lastLog = Time.time;
                int crossing = 0;
                foreach (var ped in Pedestrian.All) if (ped.IsCrossingRoad) crossing++;
                Debug.Log($"[AUTO] t={Time.time:0} pedestrians={Pedestrian.All.Count} crossing={crossing} fps={1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0}");
                foreach (var p in pilots)
                {
                    if (!p) continue;
                    var car = p.GetComponent<CarController>();
                    Debug.Log($"[AUTO] t={Time.time:0} {p.name,-22} s={p.S,6:0.0} v={car.Speed * 3.6f,5:0}km/h lane={p.Lane:0.00} " +
                              $"obst={(float.IsInfinity(p.ObstacleDistance) ? "-" : p.ObstacleDistance.ToString("0.0"))} " +
                              $"light={(p.NextLight ? p.NextLight.State + "@" + p.NextLightDistance.ToString("0") : "-")} | {p.Status} [{(p.ObstacleCollider ? p.ObstacleCollider.name : "")}]");
                }
            }
            // Scenario shots: as the robotaxi goes round the parked garbage truck and the road works.
            foreach (var (objectName, label) in new[] { ("Garbage Truck (double-parked)", "garbage_truck"), ("Road Works", "road_works"), ("City Bus (at stop)", "bus_stop") })
            {
                var landmark = GameObject.Find(objectName);
                if (!landmark) continue;
                var where = landmark.transform.childCount > 0 && !landmark.GetComponent<Collider>() ? landmark.transform.GetChild(0).position : landmark.transform.position;
                if (Vector3.Distance(where, ego.transform.position) > 22f) continue;
                if (!shotAreas.Add(label + (int)(Time.time / 60f))) continue;
                rig.mode = CameraRig.Mode.Chase;
                yield return Shot($"area_{label}_{Time.time:000}_chase");
                demo.FullViz = true;
                yield return null;
                yield return Shot($"area_{label}_{Time.time:000}_viz");
                demo.FullViz = false;
            }
            if (next < plan.Length && Time.time >= plan[next].t)
            {
                var (t, what) = plan[next];
                string name = $"shot_{next:00}_{what}";
                switch (what)
                {
                    case "viz":
                        demo.FullViz = true;
                        yield return null;
                        yield return Shot(name);
                        demo.FullViz = false;
                        break;
                    case "overview":
                        rig.enabled = false;
                        rig.transform.SetPositionAndRotation(new Vector3(0f, 150f, -190f), Quaternion.LookRotation(new Vector3(0f, -150f, 190f)));
                        yield return null;
                        yield return Shot(name);
                        rig.enabled = true;
                        break;
                    case "person":
                        Pedestrian target = null;
                        foreach (var ped in Pedestrian.All) if (ped.IsCrossingRoad) { target = ped; break; }
                        if (target == null && Pedestrian.All.Count > 0) target = Pedestrian.All[0];
                        if (target) yield return CloseUp(target.transform, name, 3.2f);
                        break;
                    case "car":
                        foreach (var p in pilots)
                            if (p && p.GetComponent<VizTracked>().kind != VizTracked.Kind.Ego) { yield return CloseUp(p.transform, name, 6f); break; }
                        break;
                    default:
                        rig.mode = what switch
                        {
                            "orbit" => CameraRig.Mode.Orbit,
                            "top" => CameraRig.Mode.TopDown,
                            "driver" => CameraRig.Mode.Driver,
                            _ => CameraRig.Mode.Chase,
                        };
                        yield return new WaitForSeconds(0.6f);
                        yield return Shot(name);
                        break;
                }
                next++;
            }
            yield return null;
        }
        Application.Quit();
    }
}
