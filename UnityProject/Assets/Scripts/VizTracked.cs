using UnityEngine;

// Marks an object that the driving visualization should draw (vehicles, people, obstacles).
public class VizTracked : MonoBehaviour
{
    public enum Kind { Ego, Vehicle, Truck, Pedestrian, Obstacle }
    public Kind kind = Kind.Obstacle;
}
