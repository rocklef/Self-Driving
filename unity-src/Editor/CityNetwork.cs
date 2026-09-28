using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The downtown road network: a grid of one-way, two-lane streets.
//
//        H ◄──────────── G ◄──────────── F ◄──────────── E        z = +60
//        │               ▲               │               ▲
//        ▼   west block  │  centre park  ▼  east block   │
//        │               │               │               │
//        A ────────────► B ────────────► C ────────────► D        z = -60
//     x = -120         -40              +40             +120
//
// Every junction is either a split (B, F: go straight or turn) or a merge (C, G: a side
// avenue joins; these have traffic signals). Vehicles follow closed routes through it; each
// route becomes one RoadPath, so the driving code works exactly as on a single loop.
public class CityNetwork
{
    public const float TurnRadius = 14f, HalfRoad = 4f, OuterWalk = 7f, Curb = 0.14f;

    public static readonly Dictionary<string, Vector3> Nodes = new Dictionary<string, Vector3>
    {
        ["A"] = new Vector3(-120, 0, -60), ["B"] = new Vector3(-40, 0, -60), ["C"] = new Vector3(40, 0, -60), ["D"] = new Vector3(120, 0, -60),
        ["E"] = new Vector3(120, 0, 60), ["F"] = new Vector3(40, 0, 60), ["G"] = new Vector3(-40, 0, 60), ["H"] = new Vector3(-120, 0, 60),
    };

    public static readonly string[] Edges = { "A-B", "B-C", "C-D", "D-E", "E-F", "F-G", "G-H", "H-A", "B-G", "F-C" };

    public static readonly Dictionary<string, string[]> Routes = new Dictionary<string, string[]>
    {
        ["Perimeter"] = new[] { "A", "B", "C", "D", "E", "F", "G", "H" },
        ["West loop"] = new[] { "A", "B", "G", "H" },
        ["East loop"] = new[] { "C", "D", "E", "F" },
        ["Grand tour"] = new[] { "A", "B", "C", "D", "E", "F", "C", "D", "E", "F", "G", "H", "A", "B", "G", "H" },
    };

    // City blocks (raised concrete pads between the streets): x/z extents and corner radius.
    public static readonly (string name, float x0, float x1, float z0, float z1, float r)[] Blocks =
    {
        ("West Block", -116f, -44f, -56f, 56f, TurnRadius - HalfRoad),
        ("Centre Park", -36f, 36f, -56f, 56f, 3f),
        ("East Block", 44f, 116f, -56f, 56f, TurnRadius - HalfRoad),
    };

    public readonly Dictionary<string, RoadPath> streets = new Dictionary<string, RoadPath>();
    public readonly Dictionary<string, RoadPath> routes = new Dictionary<string, RoadPath>();
    public RoadPath perimeter;

    static Vector3 Dir(string u, string v) => (Nodes[v] - Nodes[u]).normalized;
    public static Vector3 EdgeStart(string u, string v) => Nodes[u] + Dir(u, v) * TurnRadius;
    public static Vector3 EdgeEnd(string u, string v) => Nodes[v] - Dir(u, v) * TurnRadius;

    // Adds a straight line a->b sampled every metre (b itself excluded).
    static void Line(List<Vector3> pts, Vector3 a, Vector3 b)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(a, b)));
        for (int i = 0; i < n; i++) pts.Add(Vector3.Lerp(a, b, (float)i / n));
    }

    // The path through `node` when arriving from `prev` and leaving to `next`: a 90° arc of
    // TurnRadius when turning, a straight line when going through (end point excluded).
    static void Connector(List<Vector3> pts, string prev, string node, string next)
    {
        Vector3 din = Dir(prev, node), dout = Dir(node, next), c = Nodes[node];
        Vector3 e = c - din * TurnRadius, x = c + dout * TurnRadius;
        if (Vector3.Dot(din, dout) > 0.99f) { Line(pts, e, x); return; }
        Vector3 centre = e + dout * TurnRadius;
        int n = Mathf.RoundToInt(TurnRadius * Mathf.PI * 0.5f);
        for (int i = 0; i < n; i++) pts.Add(centre + Vector3.Slerp(-dout, din, (float)i / n) * TurnRadius);
    }

    public static List<Vector3> RoutePoints(string[] nodes)
    {
        var pts = new List<Vector3>();
        int k = nodes.Length;
        for (int i = 0; i < k; i++)
        {
            string prev = nodes[(i - 1 + k) % k], cur = nodes[i], next = nodes[(i + 1) % k];
            Connector(pts, prev, cur, next);
            Line(pts, EdgeStart(cur, next), EdgeEnd(cur, next));
        }
        return pts;
    }

    static RoadPath NewPath(string name, List<Vector3> pts, bool closed, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var p = go.AddComponent<RoadPath>();
        p.SetPoints(pts, closed);
        return p;
    }

    // Every movement (arrive-from, node, leave-to) used by some route.
    static IEnumerable<(string prev, string node, string next)> Movements()
    {
        var set = new HashSet<(string, string, string)>();
        foreach (var r in Routes.Values)
            for (int i = 0; i < r.Length; i++)
                set.Add((r[(i - 1 + r.Length) % r.Length], r[i], r[(i + 1) % r.Length]));
        return set;
    }

    static bool IsCorner(string node) => Movements().Count(m => m.node == node) == 1;

    // Builds streets, routes, road surfaces, markings, block pads and the outer sidewalk.
    // `strip` draws a ribbon along a path (the scene builder's mesh helper).
    public void Build(Transform parent, System.Action<string, RoadPath, System.Action<RoadMesh>> emit)
    {
        var streetRoot = new GameObject("Streets").transform;
        streetRoot.SetParent(parent, false);
        var routeRoot = new GameObject("Routes").transform;
        routeRoot.SetParent(parent, false);
        var temp = new GameObject("temp").transform;

        foreach (var e in Edges)
        {
            var uv = e.Split('-');
            var pts = new List<Vector3>();
            Line(pts, EdgeStart(uv[0], uv[1]), EdgeEnd(uv[0], uv[1]));
            pts.Add(EdgeEnd(uv[0], uv[1]));
            streets[e] = NewPath(e, pts, false, streetRoot);
        }
        foreach (var kv in Routes) routes[kv.Key] = NewPath(kv.Key, RoutePoints(kv.Value), true, routeRoot);
        perimeter = routes["Perimeter"];

        // Straight streets: asphalt, edge lines, dashed divider, one-way arrows.
        foreach (var e in Edges)
        {
            var p = streets[e];
            emit("street " + e, p, m => { m.asphalt = true; m.edgeLines = true; m.divider = true; m.arrows = p.Length > 30f; });
        }
        // Junction and corner connectors.
        foreach (var (prev, node, next) in Movements())
        {
            var pts = new List<Vector3>();
            Connector(pts, prev, node, next);
            pts.Add(Nodes[node] + Dir(node, next) * TurnRadius);
            var p = NewPath($"{prev}-{node}-{next}", pts, false, temp);
            bool corner = IsCorner(node), straight = Vector3.Dot(Dir(prev, node), Dir(node, next)) > 0.99f;
            emit("connector " + p.name, p, m =>
            {
                m.asphalt = true;
                m.lower = !corner && !straight;          // turn arcs sit just under the through road
                m.edgeLines = corner;
                m.divider = corner || straight;
            });
        }
        // Junction fills: each street that meets a junction carries its asphalt right to the node.
        foreach (var node in Nodes.Keys.Where(n => !IsCorner(n)))
            foreach (var e in Edges)
            {
                var uv = e.Split('-');
                if (uv[0] != node && uv[1] != node) continue;
                Vector3 d = uv[0] == node ? Dir(node, uv[1]) : Dir(node, uv[0]);
                var pts = new List<Vector3>();
                Line(pts, Nodes[node] - d * HalfRoad, Nodes[node] + d * TurnRadius);
                pts.Add(Nodes[node] + d * TurnRadius);
                var p = NewPath($"fill {node} {e}", pts, false, temp);
                emit("fill " + p.name, p, m => { m.asphalt = true; m.lower = true; });
            }
        // Outer sidewalk ring and curb (the perimeter runs anticlockwise, so its right side is outside).
        emit("outer sidewalk", perimeter, m => { m.outerWalk = true; });

        Object.DestroyImmediate(temp.gameObject);
    }

    // What to draw along one path (filled in by Build, drawn by the scene builder).
    public class RoadMesh
    {
        public bool asphalt, lower, edgeLines, divider, arrows, outerWalk;
    }

    // Rounded-rectangle outline, clockwise seen from above (so a fan of it faces up).
    public static List<Vector3> RoundedRect(float x0, float x1, float z0, float z1, float r, float y)
    {
        var pts = new List<Vector3>();
        void Corner(float cx, float cz, float a0, float a1)
        {
            int n = Mathf.Max(2, Mathf.RoundToInt(r * 1.2f));
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Lerp(a0, a1, (float)i / n) * Mathf.Deg2Rad;
                pts.Add(new Vector3(cx + Mathf.Cos(a) * r, y, cz + Mathf.Sin(a) * r));
            }
        }
        Corner(x1 - r, z1 - r, 90f, 0f);
        Corner(x1 - r, z0 + r, 0f, -90f);
        Corner(x0 + r, z0 + r, -90f, -180f);
        Corner(x0 + r, z1 - r, 180f, 90f);
        return pts;
    }
}
