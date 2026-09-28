using System.Collections.Generic;
using UnityEngine;

// Lidar-style ray sensor: a dense fan ahead plus a sparse ring around the car.
// Rays start inside the car's own collider, and Unity never reports that collider,
// so the car does not see itself.
public class SensorArray : MonoBehaviour
{
    public struct SensorHit
    {
        public float angle, range, distance, height;
        public bool hit;
        public Vector3 point;
        public Collider collider;
    }

    public float height = 0.9f;
    public float lowHeight = 0.3f;     // a second front fan for low things (boxes, crates)
    public float frontRange = 50f, frontHalfAngle = 30f, frontStep = 3f;
    public float ringRange = 25f;
    public int ringCount = 16;
    public bool showRays = true;
    public static bool HideAll;   // set by the camera while it is inside the car
    public Color clearColor = new Color(0.2f, 1f, 0.7f, 0.35f);
    public Color hitColor = new Color(1f, 0.2f, 0.15f, 0.95f);

    public readonly List<SensorHit> Rays = new List<SensorHit>();
    readonly List<LineRenderer> lines = new List<LineRenderer>();

    void Awake()
    {
        for (float a = -frontHalfAngle; a <= frontHalfAngle + 0.01f; a += frontStep)
            Rays.Add(new SensorHit { angle = a, range = frontRange, distance = frontRange, height = height });
        for (float a = -frontHalfAngle; a <= frontHalfAngle + 0.01f; a += frontStep)
            Rays.Add(new SensorHit { angle = a, range = frontRange * 0.8f, distance = frontRange * 0.8f, height = lowHeight });
        for (int i = 0; i < ringCount; i++)
        {
            float a = Mathf.DeltaAngle(0f, i * 360f / ringCount);
            if (Mathf.Abs(a) > frontHalfAngle + 1f)
                Rays.Add(new SensorHit { angle = a, range = ringRange, distance = ringRange, height = height });
        }

        var mat = new Material(Shader.Find("Sprites/Default"));
        for (int i = 0; i < Rays.Count; i++)
        {
            var go = new GameObject("SensorRay");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = mat;
            lr.positionCount = 2;
            lr.widthMultiplier = 0.035f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.useWorldSpace = true;
            lines.Add(lr);
        }
    }

    void FixedUpdate()
    {
        for (int i = 0; i < Rays.Count; i++)
        {
            var r = Rays[i];
            Vector3 origin = transform.position + Vector3.up * r.height;
            Vector3 dir = Quaternion.AngleAxis(r.angle, Vector3.up) * transform.forward;
            if (Physics.Raycast(origin, dir, out RaycastHit h, r.range, ~0, QueryTriggerInteraction.Ignore))
            {
                r.hit = true; r.distance = h.distance; r.point = h.point; r.collider = h.collider;
            }
            else
            {
                r.hit = false; r.distance = r.range; r.point = origin + dir * r.range; r.collider = null;
            }
            Rays[i] = r;
        }
    }

    void LateUpdate()
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var lr = lines[i];
            lr.enabled = showRays && !HideAll;
            if (!lr.enabled) continue;
            var r = Rays[i];
            Vector3 origin = transform.position + Vector3.up * r.height;
            // Rebuild from the rendered pose so the lines don't lag the interpolated car.
            Vector3 end = origin + Quaternion.AngleAxis(r.angle, Vector3.up) * transform.forward * r.distance;
            lr.SetPosition(0, origin);
            lr.SetPosition(1, end);
            Color c = r.hit ? Color.Lerp(hitColor, clearColor, r.distance / r.range) : clearColor;
            lr.startColor = c;
            lr.endColor = r.hit ? c : new Color(c.r, c.g, c.b, 0.03f);
        }
    }
}
