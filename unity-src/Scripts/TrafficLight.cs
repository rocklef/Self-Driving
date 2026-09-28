using System.Collections.Generic;
using UnityEngine;

// Cycles green -> yellow -> red and broadcasts its state to cars (V2I).
public class TrafficLight : MonoBehaviour
{
    public enum Phase { Green, Yellow, Red }
    public static readonly List<TrafficLight> All = new List<TrafficLight>();

    public Vector3 stopPoint;           // centre of the stop line
    public Vector3 approachDir;         // direction traffic drives when it reaches the stop line
    public float greenTime = 10f, yellowTime = 3f, redTime = 8f, startOffset;
    public Renderer redLamp, yellowLamp, greenLamp;
    public Light glow;

    public Phase State { get; private set; }
    public float TimeLeft { get; private set; }

    static readonly Color Red = new Color(1f, 0.08f, 0.05f);
    static readonly Color Yellow = new Color(1f, 0.7f, 0.05f);
    static readonly Color Green = new Color(0.1f, 1f, 0.35f);

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void Update()
    {
        float cycle = greenTime + yellowTime + redTime;
        float t = Mathf.Repeat(Time.time + startOffset, cycle);
        if (t < greenTime) { State = Phase.Green; TimeLeft = greenTime - t; }
        else if (t < greenTime + yellowTime) { State = Phase.Yellow; TimeLeft = greenTime + yellowTime - t; }
        else { State = Phase.Red; TimeLeft = cycle - t; }

        SetLamp(redLamp, Red, State == Phase.Red);
        SetLamp(yellowLamp, Yellow, State == Phase.Yellow);
        SetLamp(greenLamp, Green, State == Phase.Green);
        if (glow)
        {
            var active = State == Phase.Red ? redLamp : State == Phase.Yellow ? yellowLamp : greenLamp;
            glow.color = State == Phase.Red ? Red : State == Phase.Yellow ? Yellow : Green;
            if (active) glow.transform.position = active.transform.position + transform.forward * 0.5f;
        }
    }

    public static void SetLamp(Renderer r, Color c, bool on)
    {
        if (!r) return;
        var m = r.material;
        Color body = on ? c : c * 0.12f;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", body);
        if (m.HasProperty("_Color")) m.SetColor("_Color", body);
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", on ? c * 6f : Color.black);
    }
}
