using System.Collections.Generic;
using UnityEngine;

// A zebra crossing tied to a traffic light: pedestrians may start crossing while cars have red.
public class Crosswalk : MonoBehaviour
{
    public static readonly List<Crosswalk> All = new List<Crosswalk>();

    public TrafficLight signal;
    public RoadPath road;
    public float s0, s1;                                  // extent along the road
    public Renderer[] walkLamps, dontWalkLamps;

    static readonly Color WalkColor = new Color(0.85f, 0.95f, 1f);
    static readonly Color DontWalkColor = new Color(1f, 0.45f, 0.05f);

    public Vector3 Center => road.Center((s0 + s1) * 0.5f);
    public Vector3 Direction => road.Tangent((s0 + s1) * 0.5f);

    // Walk only while cars have red, with enough time left to get across.
    public bool Walk => signal && signal.State == TrafficLight.Phase.Red && signal.TimeLeft > 4f;

    public bool Occupied
    {
        get
        {
            foreach (var p in Pedestrian.All) if (p.CrossingAt == this) return true;
            return false;
        }
    }

    // Is a vehicle on the zebra, or over the stop line heading onto it? People wait rather than
    // walk into it. (Cars waiting properly stop a metre before the line, outside this box.)
    public bool VehicleOnCrossing
    {
        get
        {
            if (checkedFrame == Time.frameCount || !road) return vehicleOn;
            checkedFrame = Time.frameCount;
            vehicleOn = false;
            float from = s0 - 2.3f, to = s1 + 0.5f, mid = (from + to) * 0.5f;
            for (int lane = 0; lane < 2 && !vehicleOn; lane++)
            {
                int n = Physics.OverlapBoxNonAlloc(road.Point(mid, lane) + Vector3.up, new Vector3(1.4f, 0.8f, (to - from) * 0.5f),
                    hits, Quaternion.LookRotation(road.Tangent(mid)), ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                    if (hits[i].attachedRigidbody && hits[i].attachedRigidbody.GetComponent<CarController>()) { vehicleOn = true; break; }
            }
            return vehicleOn;
        }
    }
    int checkedFrame = -1;
    bool vehicleOn;
    readonly Collider[] hits = new Collider[16];

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Update()
    {
        bool walk = Walk;
        // Flash "don't walk" during the last seconds of red, like real signals.
        bool flashing = signal && signal.State == TrafficLight.Phase.Red && !walk;
        bool dontWalkOn = !walk && (!flashing || Mathf.Repeat(Time.time, 1f) < 0.5f);
        foreach (var r in walkLamps) TrafficLight.SetLamp(r, WalkColor, walk);
        foreach (var r in dontWalkLamps) TrafficLight.SetLamp(r, DontWalkColor, dontWalkOn);
    }
}
