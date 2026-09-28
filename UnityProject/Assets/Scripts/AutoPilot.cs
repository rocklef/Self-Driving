using System.Collections.Generic;
using UnityEngine;

// The self-driving brain, used by the robotaxi and every traffic vehicle. Each physics step it:
//  1. localises the car on the road (S = metres along the one-way loop, lateral lane position),
//  2. finds the nearest thing on its *planned* path (lidar rays plus a solid sweep of the path,
//     so even small, low objects are never missed) and predicts people stepping onto the road,
//  3. reads traffic lights (V2I) and the speed and intent of the vehicle ahead (V2V),
//  4. decides whether to go round a blocked lane or overtake a slow vehicle, waiting for a safe
//     gap first, and returns to the right lane afterwards,
//  5. picks the lowest of all speed limits and steers with pure pursuit along the plan.
// Lane changes are planned over road distance (an S-curve), never over time, and are fitted to
// finish before the obstacle. Cars never reverse: they stop far enough back to steer round
// anything parked, and if they do end up close they pull out slowly at full lock.
// Traffic rules: stop on red (and on yellow unless too close to stop), yield at zebra crossings,
// keep right, overtake only on straight road away from crossings and signals, never overtake a
// queue, and never stop on a crossing or inside a junction (wait at the line until there is room).
[DefaultExecutionOrder(-10)]
[RequireComponent(typeof(CarController), typeof(SensorArray))]
public class AutoPilot : MonoBehaviour
{
    public enum Limit { Cruise, Curve, Follow, Obstacle, Light, Pedestrian, Crosswalk, Manual, KeepClear }

    public RoadPath road;
    public float cruiseSpeed = 13f;
    public bool canChangeLanes = true;
    public int startLane = 0;
    public float comfortDecel = 3.5f;
    public float followGap = 4f;       // bumper gap behind a queued vehicle
    public float lateralAccel = 2.8f;

    public string Status { get; private set; } = "Starting";
    public Limit Reason { get; private set; }
    public float S { get; private set; }
    public float Lane { get; private set; }                 // 0 = right lane, 1 = left lane, fractions while changing
    public int TargetLane { get; private set; }
    public int PendingLane { get; private set; } = -1;      // a lane we want but cannot enter yet
    public bool ChangingLanes => changing;
    public float ObstacleDistance { get; private set; } = float.PositiveInfinity;
    public float ObstacleSpeed { get; private set; }
    public bool ObstacleIsPedestrian { get; private set; }
    public Collider ObstacleCollider { get; private set; }
    public string ObstacleName { get; private set; } = "";
    public TrafficLight NextLight { get; private set; }
    public float NextLightDistance { get; private set; } = float.PositiveInfinity;
    public Crosswalk YieldingTo { get; private set; }
    public float DesiredSpeed { get; private set; }
    public float StopDistance { get; private set; } = float.PositiveInfinity;
    public Vector3 LookTarget { get; private set; }
    public string GapBlocker { get; private set; } = "";
    public string LaneWhy { get; private set; } = "";      // why no lane change was started (diagnostics)   // what last kept a lane from being clear (diagnostics)

    // True when this car is held up by traffic (a queue, a light, people, a blockage) rather than
    // cruising slowly by choice. Cars behind use it (V2V) to decide not to overtake a queue.
    public bool IsQueued =>
        Reason is Limit.Follow or Limit.Obstacle or Limit.Light or Limit.Pedestrian or Limit.Crosswalk or Limit.KeepClear
        && DesiredSpeed < cruiseSpeed * 0.8f;

    // Held up by something parked in its lane: it will pull out round it, so a car behind must stay
    // far enough back to be able to follow it round.
    public bool BlockedByStatic => Reason == Limit.Obstacle || PendingLane >= 0 && leadPilot == null;

    CarController car;
    SensorArray sensors;
    BoxCollider body;
    Collider[] ownColliders;
    AutoPilot leadPilot;
    readonly Collider[] overlap = new Collider[32];
    readonly HashSet<Collider> seen = new HashSet<Collider>();
    float halfLength = 2.1f, halfWidth = 1f;
    float minChange = 9f, minClear = 10.5f, obstacleGap = 12f, minTight = 10f;
    float laneCooldown, manualSteer, manualThrottle;

    // lane-change plan
    bool changing, tight;
    float fromLane;
    int toLane;
    float changeStartS, changeLength;
    string passName = "";

    // signals and zebra crossings on this vehicle's own route (a route can meet one more than once)
    readonly List<(TrafficLight tl, float s)> routeLights = new List<(TrafficLight, float)>();
    readonly List<(Crosswalk cw, float s0, float s1)> routeCrossings = new List<(Crosswalk, float, float)>();
    // Keep-clear boxes after each stop line: a zebra crossing, or a junction up to the next street.
    readonly List<(float s, float length)> keepClear = new List<(float, float)>();
    // Split junctions on this route (start and end), where only the right lane may be used.
    readonly List<(float s0, float s1)> splits = new List<(float, float)>();

    void Start()
    {
        car = GetComponent<CarController>();
        sensors = GetComponent<SensorArray>();
        body = GetComponent<BoxCollider>();
        ownColliders = GetComponentsInChildren<Collider>();
        if (body) { halfLength = body.size.z * 0.5f; halfWidth = body.size.x * 0.5f; }
        // Shortest comfortable S-curve for this vehicle (two arcs at a 30° steering angle, 3.6 m sideways),
        // and how far back to stop from something it has to steer round.
        float r = car.wheelBase / Mathf.Tan(30f * Mathf.Deg2Rad);
        float half = road.laneWidth * 0.5f;
        minChange = Mathf.Max(8f, 2f * Mathf.Sqrt(Mathf.Max(0f, 2f * r * half - half * half)) + 1f);
        minClear = minChange + 1.5f;       // closest we can be and still fit the S-curve before it
        obstacleGap = minChange + 3f;
        // Slow pull-out at nearly full lock: a smoothstep over L has peak curvature 6 * shift / L^2.
        minTight = Mathf.Sqrt(6f * road.laneWidth * car.wheelBase / Mathf.Tan(car.maxSteerDeg * 0.85f * Mathf.Deg2Rad));
        S = road.Project(transform.position);
        Lane = TargetLane = startLane;
        foreach (var tl in TrafficLight.All)
            foreach (float s in road.FindAll(tl.stopPoint, tl.approachDir)) routeLights.Add((tl, s));
        foreach (var cw in Crosswalk.All)
        {
            float span = (cw.s1 - cw.s0) * 0.5f;
            foreach (float s in road.FindAll(cw.Center, cw.Direction)) routeCrossings.Add((cw, s - span, s + span));
        }
        foreach (var (tl, s) in routeLights)
        {
            var cw = Crosswalk.All.Find(c => c.signal == tl);
            // a crossing: its stripes; a junction: through the turn to where the next street starts
            // (junction: 1 m to the street end + 28 m through the turn, plus a margin)
            keepClear.Add((s, cw ? cw.s1 - cw.s0 + 2.5f + 1.5f : 31f));
        }
        foreach (var j in Junction.All)
        {
            if (!j.split) continue;
            float start = -1f;
            for (float s = 0f; s <= road.Length; s += 1f)
            {
                bool inside = s < road.Length && Vector3.Distance(road.Center(s), j.transform.position) < j.radius;
                if (inside && start < 0f) start = s;
                if (!inside && start >= 0f) { splits.Add((start, s)); start = -1f; }
            }
        }
    }

    // Does a split junction on our route overlap the stretch from..to metres ahead?
    bool SplitBetween(float from, float to)
    {
        foreach (var (s0, s1) in splits)
        {
            float a = Rel(s0), b = Rel(s1);
            if (b < a) b += road.Length;   // (the loop seam falls inside it)
            if (b >= from && a <= to) return true;
        }
        return false;
    }

    // Metres from our front bumper to the next split junction ahead (infinity if none within 80 m).
    float NextSplit()
    {
        float best = float.PositiveInfinity;
        foreach (var (s0, _) in splits)
        {
            float d = Rel(s0) - halfLength;
            if (d >= 0f && d < 80f && d < best) best = d;
        }
        return best;
    }

    // Is route position s within `before` metres ahead of / `after` metres past a zebra crossing?
    public bool NearCrossing(float s, float before, float after)
    {
        foreach (var (_, s0, s1) in routeCrossings)
        {
            float a = road.Wrap(s0 - s), b = road.Wrap(s - s1);
            if (a > road.Length * 0.5f) a -= road.Length;
            if (b > road.Length * 0.5f) b -= road.Length;
            if (a < before && b < after) return true;
        }
        return false;
    }

    void Update()
    {
        if (car.autopilotEngaged) return;
        manualSteer = (KeyInput.Held(KeyCode.D) || KeyInput.Held(KeyCode.RightArrow) ? 1f : 0f)
                    - (KeyInput.Held(KeyCode.A) || KeyInput.Held(KeyCode.LeftArrow) ? 1f : 0f);
        manualThrottle = (KeyInput.Held(KeyCode.W) || KeyInput.Held(KeyCode.UpArrow) ? 1f : 0f)
                       - (KeyInput.Held(KeyCode.S) || KeyInput.Held(KeyCode.DownArrow) ? 1f : 0f);
    }

    // Signed distance along the loop from us to s (negative = behind).
    float Rel(float s)
    {
        float d = road.Wrap(s - S);
        return d > road.Length * 0.5f ? d - road.Length : d;
    }

    // Planned lateral lane position at road position s (the S-curve of a lane change in progress).
    public float LaneAt(float s)
    {
        if (!changing) return Lane;
        float d = road.Wrap(s - changeStartS);
        if (d > road.Length * 0.5f) d -= road.Length;
        return Mathf.Lerp(fromLane, toLane, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / changeLength)));
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        S = road.Project(transform.position, S);
        if (changing)
        {
            Lane = LaneAt(S);
            float done = road.Wrap(S - changeStartS);
            if (done >= changeLength && done < road.Length * 0.5f) { changing = false; Lane = toLane; laneCooldown = 1.5f; }
        }
        SenseObstacle();
        CheckTrafficLights();

        if (!car.autopilotEngaged)
        {
            float accel = manualThrottle > 0f ? car.maxAccel : manualThrottle < 0f ? -car.maxBrake * 0.6f : -0.8f;
            car.allowReverse = false;
            car.Command(manualSteer * car.maxSteerDeg, accel);
            changing = false;
            Lane = Mathf.Clamp01(0.5f - road.Lateral(transform.position, S) / road.laneWidth);
            TargetLane = Mathf.RoundToInt(Lane);
            PendingLane = -1;
            DesiredSpeed = car.Speed;
            Reason = Limit.Manual;
            Status = "Manual control";
            return;
        }

        car.allowReverse = false;

        float v = car.Speed;
        laneCooldown -= dt;
        DecideLane(v);

        // ---- speed: the lowest of every limit wins ----
        float desired = cruiseSpeed;
        Reason = Limit.Cruise;
        Status = changing ? (toLane > fromLane ? $"Overtaking {passName}" : "Returning to right lane") : "Cruising";

        float k = road.MaxCurvature(S, S + 8f + v * 2.5f);
        if (k > 1e-3f)
        {
            float vc = Mathf.Sqrt(lateralAccel / k);
            if (vc < desired) { desired = vc; Reason = Limit.Curve; if (!changing) Status = "Slowing for curve"; }
        }
        if (changing && tight && desired > 2.5f) { desired = 2.5f; Status = $"Pulling out round {passName}"; }

        float stopDist = float.PositiveInfinity;   // where we must be stopped by (bumper distance)

        // Still in the left lane near a split junction: wait before it until we can merge right.
        if (LaneAt(S + 20f) > 0.5f)
        {
            float dj = NextSplit() - 16f;   // leave straight road to change lanes on
            if (dj < 35f)
            {
                stopDist = Mathf.Max(dj, 0f);
                float vj = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * stopDist));
                if (vj < desired) { desired = vj; Reason = Limit.KeepClear; Status = "Merging right before the junction"; }
            }
        }

        if (!float.IsInfinity(ObstacleDistance))
        {
            float d = ObstacleDistance, limit;
            if (ObstacleIsPedestrian)
            {
                stopDist = d - 5f;
                limit = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * stopDist));
                if (limit < desired) { desired = limit; Reason = Limit.Pedestrian; Status = v < 0.3f ? "Waiting for pedestrian" : "Pedestrian ahead: stopping"; }
            }
            else if (leadPilot)
            {
                float lead = ObstacleSpeed;
                float stopGap = canChangeLanes && leadPilot.BlockedByStatic && Mathf.RoundToInt(Lane) == 0 ? obstacleGap : followGap;
                float gap = stopGap + 1.2f * lead;
                limit = Mathf.Sqrt(Mathf.Max(0f, lead * lead + 2f * comfortDecel * (d - gap)));
                if (lead < 0.5f) stopDist = d - stopGap;
                if (limit < desired)
                {
                    desired = limit;
                    Reason = Limit.Follow;
                    Status = PendingLane >= 0 ? $"Waiting for a gap to pass {ObstacleName}"
                        : lead > 1f ? $"Following {ObstacleName}" : $"Queued behind {ObstacleName}";
                }
            }
            else
            {
                // Something parked or dropped: stop far enough back to steer round it.
                float gap = canChangeLanes ? obstacleGap : followGap;
                stopDist = d - gap;
                limit = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * stopDist));
                if (limit < desired)
                {
                    desired = limit;
                    Reason = Limit.Obstacle;
                    Status = PendingLane >= 0 ? $"Waiting for a gap to pass {ObstacleName}"
                        : v < 0.3f ? $"Stopped: {ObstacleName} ahead" : $"Slowing for {ObstacleName}";
                }
            }
        }

        if (NextLight != null && NextLight.State != TrafficLight.Phase.Green)
        {
            float d = NextLightDistance - 1f;   // stop a metre short of the line
            bool committed = NextLight.State == TrafficLight.Phase.Yellow && d < v * v / (2f * comfortDecel) * 0.7f;
            if (!committed)
            {
                stopDist = Mathf.Min(stopDist, d);
                float vl = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * d));
                if (vl < desired)
                {
                    desired = vl;
                    Reason = Limit.Light;
                    Status = v < 0.3f ? "Waiting at red light"
                        : (NextLight.State == TrafficLight.Phase.Red ? "Red" : "Yellow") + " light ahead: stopping";
                }
            }
        }

        // Keep clear: never stop on a zebra crossing or inside a junction. If the traffic ahead is
        // standing past the stop line and there is no room for the whole car beyond the box, wait
        // at the line (even on green) until the queue moves on.
        if (leadPilot && ObstacleSpeed < 2f)
            foreach (var (s, length) in keepClear)
            {
                float line = Rel(s) - halfLength;          // bumper to the stop line
                if (line < 0f || line > 60f || ObstacleDistance <= line) continue;
                float stopAt = ObstacleDistance - followGap;   // where we would stop behind the queue
                if (stopAt - 2f * halfLength >= line + length) continue;   // room for us beyond the box
                stopDist = Mathf.Min(stopDist, line);
                float vk = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * line));
                if (vk < desired) { desired = vk; Reason = Limit.KeepClear; Status = v < 0.3f ? "Keeping the junction clear" : "Traffic ahead: keeping clear"; }
            }

        // Yield at the stop line while anyone is on a zebra crossing ahead, even on green. A car
        // already over the stop line still stops just short of the stripes.
        YieldingTo = null;
        foreach (var (cw, s0, _) in routeCrossings)
        {
            float d = Rel(s0 - 2.5f) - halfLength;
            if (d < 0f) d = Rel(s0 - 0.3f) - halfLength;
            if (d < 0f || d > 60f || !cw.Occupied) continue;
            YieldingTo = cw;
            stopDist = Mathf.Min(stopDist, d);
            float vp = Mathf.Sqrt(Mathf.Max(0f, 2f * comfortDecel * d));
            if (vp < desired) { desired = vp; Reason = Limit.Crosswalk; Status = v < 0.3f ? "Yielding to pedestrians" : "Pedestrians crossing: stopping"; }
        }

        DesiredSpeed = desired;
        StopDistance = stopDist;
        float a = Mathf.Clamp(2.2f * (desired - v), -car.maxBrake, car.maxAccel);
        // Feed-forward: brake exactly hard enough (v^2 / 2d) to stop at the stop point instead of lagging.
        if (!float.IsInfinity(stopDist) && v > 0.1f)
        {
            float need = v * v / (2f * Mathf.Max(stopDist, 0.25f));
            if (need > comfortDecel * 0.6f) a = Mathf.Max(Mathf.Min(a, -need), -car.maxBrake);
        }
        if (desired < 0.05f && v < 0.3f) a = -car.maxBrake;
        if (ObstacleDistance < 1.2f) { a = -car.maxBrake; Status = "EMERGENCY BRAKE"; }

        car.Command(PursuitSteer(v), a);
    }

    // Pure pursuit from the rear axle towards a point ahead on the planned path.
    float PursuitSteer(float v)
    {
        float look = Mathf.Clamp(4f + 0.45f * v, 5f, 14f);
        Vector3 rear = transform.position - transform.forward * car.wheelBase * 0.5f;
        LookTarget = road.Point(S + look, LaneAt(S + look));
        Vector3 to = LookTarget - rear;
        to.y = 0f;
        float alpha = Vector3.SignedAngle(transform.forward, to, Vector3.up) * Mathf.Deg2Rad;
        return Mathf.Atan2(2f * car.wheelBase * Mathf.Sin(alpha), to.magnitude) * Mathf.Rad2Deg;
    }

    // ------------------------------------------------------------------ lane decisions

    void DecideLane(float v)
    {
        PendingLane = -1;
        LaneWhy = "";
        if (!canChangeLanes || changing || laneCooldown > 0f) { LaneWhy = changing ? "changing" : "cooldown"; return; }
        if (road.MaxCurvature(S, S + 12f) > 0.03f) { LaneWhy = "curve"; return; }   // never change lanes on or into a turn
        int lane = Mathf.RoundToInt(Lane);
        bool blocked = !float.IsInfinity(ObstacleDistance) && ObstacleDistance < 35f;
        bool atCrossing = CrossingBetween(-8f, 25f);

        if (lane == 0)
        {
            if (!blocked || ObstacleIsPedestrian) return;
            // (something parked before a red light is still passed: we then wait at the line in the left lane)
            if (leadPilot != null && NextLight != null && NextLight.State != TrafficLight.Phase.Green && NextLightDistance < 45f) { LaneWhy = "light"; return; }
            // Go round parked vehicles and objects (anywhere: a blocked lane has to be passed);
            // overtake moving vehicles only if they are genuinely slow, never a queue, and never
            // around a zebra crossing.
            bool isStatic = leadPilot == null;
            float obstacleLength = ObstacleCollider ? ObstacleCollider.bounds.size.magnitude : 5f;
            float front = ObstacleDistance + halfLength + obstacleLength + 12f;
            if (!isStatic && (leadPilot.IsQueued || ObstacleSpeed >= cruiseSpeed * 0.8f || atCrossing || CrossingBetween(0f, front)
                              || SignalBetween(-5f, front + 10f) || SplitBetween(-5f, front + 30f))) { LaneWhy = "no overtaking here"; return; }
            if (isStatic && ObstacleDistance < minClear && v > 0.3f) { LaneWhy = "too close, stopping"; return; }   // too close for a normal change: stop first, then pull out slowly
            if (SplitBetween(-halfLength - 1f, 0f)) { LaneWhy = "in split"; return; }   // not while still in a split junction
            if (LaneClear(1, front)) StartChange(0, 1, v, ObstacleName, isStatic ? ObstacleDistance : float.PositiveInfinity);
            else PendingLane = 1;
        }
        else
        {
            // Keep right: return as soon as the right lane is free alongside and well ahead
            // (and always before a split junction, where the left lane must be empty).
            if (atCrossing && !blocked && !SplitBetween(0f, 50f)) return;
            bool staticAhead = blocked && leadPilot == null && !ObstacleIsPedestrian;
            if (staticAhead && ObstacleDistance < minClear && v > 0.3f) return;
            if (LaneClear(0, 28f)) StartChange(1, 0, v, staticAhead ? ObstacleName : "", staticAhead ? ObstacleDistance : float.PositiveInfinity);
            else if (blocked) PendingLane = 0;
        }
    }

    // Plans the S-curve. Round a static obstacle it is fitted to finish 1.5 m before reaching it.
    void StartChange(float from, int to, float v, string what, float obstacleAhead)
    {
        changing = true;
        fromLane = from;
        toLane = to;
        TargetLane = to;
        changeStartS = S;
        changeLength = Mathf.Clamp(v * 2.2f, minChange, 24f);
        tight = !float.IsInfinity(obstacleAhead) && obstacleAhead < minClear;
        if (!float.IsInfinity(obstacleAhead)) changeLength = Mathf.Clamp(obstacleAhead - 1.5f, tight ? minTight : minChange, changeLength);
        passName = string.IsNullOrEmpty(what) ? "slow traffic" : what;
    }

    bool CrossingBetween(float from, float to)
    {
        foreach (var (_, s0, s1) in routeCrossings)
        {
            float a = Rel(s0 - 3f), b = Rel(s1 + 2f);
            if (b >= from && a <= to) return true;
        }
        return false;
    }

    // A signalised stop line (junction or crossing) between from and to metres ahead?
    bool SignalBetween(float from, float to)
    {
        foreach (var (_, s) in routeLights)
        {
            float d = Rel(s);
            if (d >= from && d <= to) return true;
        }
        return false;
    }

    // Is the given lane free from just behind us to `front` metres ahead? Moving vehicles behind
    // need a speed-dependent gap, so we never cut in front of anyone.
    bool LaneClear(int lane, float front)
    {
        seen.Clear();
        for (float x = -30f; x < front; x += 4f)
        {
            float s = S + x + 2f;
            Vector3 c = road.Point(s, lane) + Vector3.up * 1.0f;
            int n = Physics.OverlapBoxNonAlloc(c, new Vector3(1.25f, 0.9f, 2.1f), overlap,
                Quaternion.LookRotation(road.Tangent(s)), ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = overlap[i];
                if (System.Array.IndexOf(ownColliders, col) >= 0 || !seen.Add(col)) continue;
                float d = Rel(road.Project(col.bounds.center, s));
                float theirHalf = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);
                var other = col.attachedRigidbody ? col.attachedRigidbody.GetComponent<CarController>() : null;
                // a moving vehicle behind needs a speed-dependent gap; something parked only has to be
                // clear of our rear bumper (we are already past it)
                // (plus how much a faster car closes in while we change lanes, about 3 s)
                float behind = halfLength + theirHalf + (other ? 3f + Mathf.Max(0f, other.Speed) * 1.6f + Mathf.Max(0f, other.Speed - car.Speed) * 3f : 0.8f);
                if (d > -behind && d < front) { GapBlocker = $"{col.name} lane {lane} at {d:0.0} m"; return false; }
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ perception

    void SenseObstacle()
    {
        float best = float.PositiveInfinity;
        Collider bestCol = null;

        // 1. Lidar returns on the planned path, plus people about to step into it.
        foreach (var r in sensors.Rays)
        {
            if (!r.hit || r.collider == null) continue;
            float sHit = road.Project(r.point, S + r.distance);
            float ahead = Rel(sHit);
            if (ahead < halfLength * 0.6f || ahead > 60f) continue;   // beside or behind us, or too far
            float lat = road.Lateral(r.point, sHit);
            bool inPath = Mathf.Abs(lat - road.LaneOffset(LaneAt(sHit))) <= halfWidth + 0.45f;
            var ped = r.collider.GetComponentInParent<Pedestrian>();
            bool enteringRoad = ped && ped.IsCrossingRoad && Mathf.Abs(lat) < 5.5f && ahead < 40f;
            if (!inPath && !enteringRoad) continue;
            float d = ahead - halfLength;
            if (d < best) { best = d; bestCol = r.collider; }
        }

        // 2. Solid sweep of the planned path, 10 cm and up, so nothing small or low slips between rays.
        seen.Clear();
        Vector3 front = transform.position + transform.forward * halfLength;
        for (float x = halfLength + 1f; x < 46f && x - halfLength - 1f < best; x += 2f)
        {
            float s = S + x;
            Vector3 c = road.Point(s, LaneAt(s)) + Vector3.up * 1.0f;
            int n = Physics.OverlapBoxNonAlloc(c, new Vector3(halfWidth + 0.25f, 0.9f, 1f), overlap,
                Quaternion.LookRotation(road.Tangent(s)), ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = overlap[i];
                if (System.Array.IndexOf(ownColliders, col) >= 0 || !seen.Add(col)) continue;
                float d = Rel(road.Project(col.ClosestPoint(front), s)) - halfLength;
                if (d < best) { best = d; bestCol = col; }
            }
        }

        ObstacleDistance = best;
        ObstacleCollider = bestCol;
        ObstacleIsPedestrian = bestCol && bestCol.GetComponentInParent<Pedestrian>();
        leadPilot = bestCol && bestCol.attachedRigidbody ? bestCol.attachedRigidbody.GetComponent<AutoPilot>() : null;
        // V2V: a car ahead broadcasts its speed; anything else is stationary.
        ObstacleSpeed = leadPilot ? Mathf.Max(0f, leadPilot.GetComponent<CarController>().Speed) : 0f;
        ObstacleName = bestCol ? Pretty(bestCol) : "";
    }

    static string Pretty(Collider c)
    {
        if (c.GetComponentInParent<Pedestrian>()) return "pedestrian";
        var t = c.attachedRigidbody ? c.attachedRigidbody.transform : c.transform;
        string n = t.name;
        int cut = n.IndexOf(" (");
        return cut > 0 ? n.Substring(0, cut) : n;
    }

    void CheckTrafficLights()
    {
        NextLight = null;
        NextLightDistance = float.PositiveInfinity;
        foreach (var (tl, s) in routeLights)
        {
            float d = Rel(s) - halfLength;
            if (d < 0f || d > 80f) continue;
            if (d < NextLightDistance) { NextLightDistance = d; NextLight = tl; }
        }
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(LookTarget + Vector3.up * 0.5f, 0.3f);
    }
}
