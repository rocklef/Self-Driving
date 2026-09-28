using System.Collections.Generic;
using UnityEngine;

// A road centreline sampled about every metre: either a closed loop (a vehicle's route through the
// network) or an open street segment (used to place crosswalks, pedestrians and parked things).
// A position along it is "s" (metres from the start). Lane 0 is the right lane, lane 1 the left
// lane; fractional lane values are used while a car is moving between them.
public class RoadPath : MonoBehaviour
{
    public float laneWidth = 3.6f;
    public bool closed = true;
    [HideInInspector] public List<Vector3> points = new List<Vector3>();

    float[] cumulative;

    int SegmentCount => closed ? points.Count : points.Count - 1;

    public float Length { get { EnsureBuilt(); return cumulative[cumulative.Length - 1]; } }

    public void SetPoints(List<Vector3> pts, bool isClosed = true) { points = pts; closed = isClosed; cumulative = null; }

    void EnsureBuilt()
    {
        int n = SegmentCount;
        if (cumulative != null && cumulative.Length == n + 1) return;
        cumulative = new float[n + 1];
        for (int i = 0; i < n; i++)
            cumulative[i + 1] = cumulative[i] + Vector3.Distance(points[i], points[(i + 1) % points.Count]);
    }

    public float Wrap(float s)
    {
        float len = Length;
        if (!closed) return Mathf.Clamp(s, 0f, len);
        s %= len;
        return s < 0f ? s + len : s;
    }

    // Distance travelling forward from `from` to `to` (on a loop: in [0, Length)).
    public float Ahead(float from, float to) => closed ? Wrap(to - from) : to - from;

    int Segment(float s, out float t)
    {
        EnsureBuilt();
        s = Wrap(s);
        int lo = 0, hi = SegmentCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (cumulative[mid] <= s) lo = mid; else hi = mid - 1;
        }
        float seg = cumulative[lo + 1] - cumulative[lo];
        t = seg > 1e-5f ? Mathf.Clamp01((s - cumulative[lo]) / seg) : 0f;
        return lo;
    }

    Vector3 P(int i) => points[i % points.Count];

    public Vector3 Center(float s)
    {
        int i = Segment(s, out float t);
        return Vector3.Lerp(P(i), P(i + 1), t);
    }

    public Vector3 Tangent(float s)
    {
        int i = Segment(s, out _);
        return (P(i + 1) - P(i)).normalized;
    }

    public Vector3 Right(float s) => Vector3.Cross(Vector3.up, Tangent(s)).normalized;

    // Lateral offset of a lane centre from the road centreline (positive = right).
    public float LaneOffset(float lane) => laneWidth * (0.5f - lane);

    public Vector3 Point(float s, float lane) => Center(s) + Right(s) * LaneOffset(lane);

    public Vector3 PointAtOffset(float s, float lateral) => Center(s) + Right(s) * lateral;

    public float Lateral(Vector3 pos, float s)
    {
        Vector3 d = pos - Center(s);
        d.y = 0f;
        return Vector3.Dot(d, Right(s));
    }

    public float Curvature(float s, float ds = 3f)
    {
        return Vector3.Angle(Tangent(s), Tangent(s + ds)) * Mathf.Deg2Rad / ds;
    }

    public float MaxCurvature(float from, float to, float step = 2f)
    {
        float k = 0f;
        for (float s = from; s < to; s += step) k = Mathf.Max(k, Curvature(s));
        return k;
    }

    // Closest s to a world position. With a hint, only searches +-window metres around it.
    public float Project(Vector3 pos, float hint = float.NaN, float window = 30f)
    {
        EnsureBuilt();
        int n = SegmentCount;
        int start = 0, count = n;
        if (!float.IsNaN(hint))
        {
            start = Segment(hint - window, out _);
            count = Mathf.Min(n, Mathf.CeilToInt(2f * window / Mathf.Max(0.1f, Length / n)) + 2);
        }
        float bestD = float.MaxValue, bestS = 0f;
        for (int k = 0; k < count; k++)
        {
            int i = closed ? (start + k) % n : Mathf.Min(start + k, n - 1);
            Vector3 a = P(i), b = P(i + 1);
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = pos - a; ap.y = 0f;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            float d = (ap - ab * t).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                bestS = cumulative[i] + t * (cumulative[i + 1] - cumulative[i]);
            }
        }
        return bestS;
    }

    // Every place this path passes within maxDist of `pos` while heading along `dir` (a route can
    // pass the same street more than once). Used to find which signals and crossings a route meets.
    public List<float> FindAll(Vector3 pos, Vector3 dir, float maxDist = 4.5f)
    {
        EnsureBuilt();
        var found = new List<float>();
        float lastS = float.NegativeInfinity, lastD = float.MaxValue;
        for (int i = 0; i < SegmentCount; i++)
        {
            Vector3 a = P(i), b = P(i + 1);
            Vector3 ab = b - a; ab.y = 0f;
            if (ab.sqrMagnitude < 1e-6f || Vector3.Dot(ab.normalized, dir.normalized) < 0.9f) continue;
            Vector3 ap = pos - a; ap.y = 0f;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude);
            float d = (ap - ab * t).magnitude;
            if (d > maxDist) continue;
            float s = cumulative[i] + t * (cumulative[i + 1] - cumulative[i]);
            if (s - lastS < 10f)
            {
                if (d < lastD) { found[found.Count - 1] = s; lastD = d; lastS = s; }
                continue;
            }
            found.Add(s);
            lastS = s;
            lastD = d;
        }
        return found;
    }
}
