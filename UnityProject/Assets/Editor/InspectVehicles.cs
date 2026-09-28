using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode diagnostic: logs every vehicle's size and renders each one from the front
// (camera placed ahead of its forward axis), plus an overhead view of the parked truck.
public static class InspectVehicles
{
    const string Out = "../Build/inspect";

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SelfDrivingDemo.unity");
        Directory.CreateDirectory(Out);
        var road = Object.FindAnyObjectByType<RoadPath>();
        var camGo = new GameObject("InspectCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 40f;
        cam.cullingMask = ~(1 << 30);
        var rt = new RenderTexture(640, 400, 24);
        cam.targetTexture = rt;

        foreach (var v in Object.FindObjectsByType<VizTracked>(FindObjectsSortMode.None))
        {
            if (v.kind == VizTracked.Kind.Pedestrian || v.kind == VizTracked.Kind.Obstacle) continue;
            var box = v.GetComponent<BoxCollider>();
            var t = v.transform;
            float s = road.Project(t.position);
            float dot = Vector3.Dot(t.forward, road.Tangent(s));
            Debug.Log($"INSPECT {v.name}: size(w,h,l)=({box.size.x:0.00},{box.size.y:0.00},{box.size.z:0.00}) s={s:0.0} lateral={road.Lateral(t.position, s):0.00} forwardDotRoad={dot:0.00}");

            float L = box.size.z;
            cam.transform.position = t.position + t.forward * (L * 1.2f + 2f) + t.right * (L * 0.5f) + Vector3.up * 1.8f;
            cam.transform.LookAt(t.position + Vector3.up * box.size.y * 0.5f);
            Save(cam, rt, $"{Out}/front_{Safe(v.name)}.png");
        }

        // Overhead view of the double-parked truck, with the lane divider visible.
        var truck = GameObject.Find("Garbage Truck (double-parked)");
        if (truck)
        {
            cam.transform.position = truck.transform.position + Vector3.up * 28f;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.down, truck.transform.forward);
            Save(cam, rt, $"{Out}/overhead_garbage_truck.png");
        }
        Debug.Log("INSPECT done");
    }

    static string Safe(string n) => string.Join("_", n.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

    static void Save(Camera cam, RenderTexture rt, string path)
    {
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
    }
}
