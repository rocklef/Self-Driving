using UnityEngine;

// Kinematic bicycle model: the car is moved directly from speed and steering angle,
// which keeps the demo stable and predictable (no wheel-slip physics to tune).
// Speed is signed: it only goes negative (reversing) while allowReverse is set.
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    public float wheelBase = 2.6f;
    public float maxSteerDeg = 35f;
    public float steerRateDeg = 120f;
    public float maxAccel = 3.5f;
    public float maxBrake = 9f;
    public float maxSpeed = 20f;
    public float maxReverse = 2.5f;
    public bool autopilotEngaged = true;
    public bool allowReverse;

    public float Speed { get; private set; }
    public float SteerDeg { get; private set; }
    public float Accel { get; private set; }

    float targetSteer, targetAccel;
    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    public void Command(float steerDeg, float accel)
    {
        targetSteer = Mathf.Clamp(steerDeg, -maxSteerDeg, maxSteerDeg);
        targetAccel = accel;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        SteerDeg = Mathf.MoveTowards(SteerDeg, targetSteer, steerRateDeg * dt);
        Accel = Mathf.Clamp(targetAccel, -maxBrake, maxAccel);
        Speed = Mathf.Clamp(Speed + Accel * dt, allowReverse ? -maxReverse : 0f, maxSpeed);
        if (!allowReverse && Speed <= 0f && Accel < 0f) Accel = 0f;

        float yawRate = Speed / wheelBase * Mathf.Tan(SteerDeg * Mathf.Deg2Rad);
        Quaternion rot = rb.rotation * Quaternion.Euler(0f, yawRate * Mathf.Rad2Deg * dt, 0f);
        rb.MoveRotation(rot);
        rb.MovePosition(rb.position + rot * Vector3.forward * Speed * dt);
    }
}
