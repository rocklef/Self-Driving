using System.Collections.Generic;
using UnityEngine;

// A person who strolls along the sidewalk, waits at the kerb and crosses on the walk signal.
// A jaywalker (key P) crosses immediately wherever they are, then walks off and disappears.
// Movement is along the road's own coordinates (s, lateral), so walking follows curved sidewalks.
public class Pedestrian : MonoBehaviour
{
    public static readonly List<Pedestrian> All = new List<Pedestrian>();
    enum State { Pausing, ToKerb, Waiting, Crossing, Away, Leaving }

    public RoadPath road;
    public Crosswalk crosswalk;
    public Animator animator;
    public float walkSpeed = 1.3f;

    public bool Jaywalker { get; private set; }
    public bool IsCrossingRoad => state == State.Crossing;
    public Crosswalk CrossingAt => state == State.Crossing && !Jaywalker ? crosswalk : null;

    const float KerbLat = 5.0f, WalkLat = 5.7f, RoadEdge = 4f, Curb = 0.14f;

    State state = State.Pausing;
    float s, lateral, side, targetS, targetLat, crossS, timer, speed, blocked;
    bool initialised;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Start()
    {
        if (animator) animator.applyRootMotion = false;
        if (initialised) return;
        s = road.Project(transform.position);
        lateral = road.Lateral(transform.position, s);
        side = Mathf.Sign(lateral);
        timer = Random.Range(0f, 6f);
        state = State.Pausing;
        initialised = true;
    }

    // Called by the demo before activating a spawned copy.
    public void StartJaywalk(RoadPath roadPath, float atS, float fromSide)
    {
        road = roadPath;
        crosswalk = null;
        Jaywalker = true;
        s = crossS = atS;
        side = fromSide;
        lateral = side * KerbLat;
        transform.position = road.PointAtOffset(s, lateral) + Vector3.up * Curb;
        transform.rotation = Quaternion.LookRotation(-side * road.Right(s));
        Go(State.Crossing, s, -side * WalkLat);
        initialised = true;
    }

    // A vehicle standing (or rolling) within a step of where we are walking?
    static readonly Collider[] nearby = new Collider[8];
    bool VehicleInTheWay(float ds, float dl, float dist)
    {
        Vector3 next = road.PointAtOffset(s + ds / dist * 0.9f, lateral + dl / dist * 0.9f);
        int n = Physics.OverlapSphereNonAlloc(next + Vector3.up * 0.9f, 0.45f, nearby, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (nearby[i].attachedRigidbody && nearby[i].attachedRigidbody.GetComponent<CarController>()) return true;
        return false;
    }

    // Keep walks on an open street segment within its ends.
    float AlongSidewalk(float target) => road.closed ? target : Mathf.Clamp(target, 2f, road.Length - 2f);

    void Go(State next, float toS, float toLat)
    {
        state = next;
        targetS = toS;
        targetLat = toLat;
        speed = next == State.Crossing ? walkSpeed * (Jaywalker ? 1.3f : 1.1f) : walkSpeed;
    }

    void Arrived()
    {
        switch (state)
        {
            case State.ToKerb:
                state = State.Waiting;
                break;
            case State.Crossing:
                side = -side;
                if (Jaywalker) { Go(State.Leaving, s + Random.Range(8f, 14f), side * WalkLat); timer = 12f; }
                else Go(State.Away, AlongSidewalk(s + (Random.value < 0.5f ? -1f : 1f) * Random.Range(6f, 16f)), side * WalkLat);
                break;
            case State.Away:
                state = State.Pausing;
                timer = Random.Range(1f, 5f);
                break;
            case State.Leaving:
                Destroy(gameObject);
                break;
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        switch (state)
        {
            case State.Pausing:
                timer -= dt;
                if (timer <= 0f && crosswalk)
                {
                    crossS = Random.Range(crosswalk.s0 + 0.8f, crosswalk.s1 - 0.8f);
                    Go(State.ToKerb, crossS, side * Random.Range(KerbLat - 0.1f, KerbLat + 0.4f));
                }
                break;
            case State.Waiting:
                if (crosswalk && crosswalk.Walk && !crosswalk.VehicleOnCrossing) Go(State.Crossing, crossS, -side * KerbLat);
                break;
            case State.Leaving:
                timer -= dt;
                if (timer <= 0f) Destroy(gameObject);
                break;
        }

        bool moving = state == State.ToKerb || state == State.Crossing || state == State.Away || state == State.Leaving;
        Vector3 before = transform.position;
        Vector3 facing = transform.forward;
        if (moving)
        {
            float ds = targetS - s, dl = targetLat - lateral;
            float dist = Mathf.Sqrt(ds * ds + dl * dl);
            if (dist < 0.05f) Arrived();
            else if (state == State.Crossing && VehicleInTheWay(ds, dl, dist))
            {
                // A car in the way: wait a moment, then step back to the kerb we came from (the car
                // is waiting for us too) and try again when the crossing is clear.
                moving = false;
                blocked += dt;
                if (blocked > 1f)
                {
                    blocked = 0f;
                    if (Jaywalker) { Go(State.Leaving, s + Random.Range(6f, 10f), side * WalkLat); timer = 10f; }
                    else Go(State.ToKerb, s, side * KerbLat);
                }
            }
            else
            {
                blocked = 0f;
                float k = Mathf.Min(1f, speed * dt / dist);
                s += ds * k;
                lateral += dl * k;
            }
        }
        else if (state == State.Waiting)
        {
            facing = -side * road.Right(s);
        }

        Vector3 pos = road.PointAtOffset(s, lateral);
        float groundY = Mathf.Abs(lateral) > RoadEdge ? Curb : 0.02f;
        pos.y = Mathf.MoveTowards(before.y, groundY, dt * 1.5f);
        transform.position = pos;

        Vector3 step = pos - before;
        step.y = 0f;
        if (moving && step.sqrMagnitude > 1e-6f) facing = step;
        facing.y = 0f;
        if (facing.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(facing), 8f * dt);

        if (animator && animator.runtimeAnimatorController)
        {
            animator.SetBool("Walking", moving);
            animator.speed = moving ? speed / 1.35f : 1f;
        }
    }
}
