using System.Collections.Generic;
using UnityEngine;

// A junction or corner of the street grid. At a split junction one route goes straight on while
// another turns off across the left lane, so the traffic rule there is: every vehicle is back in
// the right lane before it gets here (a car turning left never cuts across one going straight),
// and no overtaking through it.
public class Junction : MonoBehaviour
{
    public static readonly List<Junction> All = new List<Junction>();
    public bool split;
    public float radius = 16f;   // the junction area: from here out to where the streets start

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);
}
