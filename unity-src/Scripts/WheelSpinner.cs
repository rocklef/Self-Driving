using UnityEngine;

// Rolls the wheels with the car's speed and turns the front ones with the steering.
// Each wheel sits under a pivot at its centre, aligned with the car (X = axle, Z = forward).
[RequireComponent(typeof(CarController))]
public class WheelSpinner : MonoBehaviour
{
    public Transform[] pivots;
    public bool[] steers;
    public float radius = 0.33f;

    CarController car;
    float angle;

    void Start() => car = GetComponent<CarController>();

    void LateUpdate()
    {
        if (pivots == null) return;
        angle = Mathf.Repeat(angle + car.Speed * Time.deltaTime / Mathf.Max(radius, 0.05f) * Mathf.Rad2Deg, 360f);
        for (int i = 0; i < pivots.Length; i++)
        {
            if (!pivots[i]) continue;
            float yaw = steers != null && i < steers.Length && steers[i] ? car.SteerDeg : 0f;
            pivots[i].localRotation = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(angle, 0f, 0f);
        }
    }
}
