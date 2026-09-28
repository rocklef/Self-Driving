using UnityEngine;

public class CameraRig : MonoBehaviour
{
    public enum Mode { Chase, Orbit, TopDown, Driver }
    public Transform target;
    public Mode mode = Mode.Chase;

    Vector3 velocity;
    float orbitAngle;

    public void Next() => mode = (Mode)(((int)mode + 1) % 4);

    void LateUpdate()
    {
        if (!target) return;
        Vector3 fwd = target.forward;
        fwd.y = 0f;
        fwd.Normalize();
        Vector3 p = target.position;
        SensorArray.HideAll = mode == Mode.Driver;

        switch (mode)
        {
            case Mode.Chase:
                transform.position = Vector3.SmoothDamp(transform.position, p - fwd * 9f + Vector3.up * 4f, ref velocity, 0.2f);
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(p + fwd * 4f + Vector3.up - transform.position), 10f * Time.deltaTime);
                break;
            case Mode.Orbit:
                orbitAngle += 15f * Time.deltaTime;
                transform.position = p + Quaternion.Euler(0f, orbitAngle, 0f) * new Vector3(0f, 4.5f, -11f);
                transform.LookAt(p + Vector3.up);
                break;
            case Mode.TopDown:
                transform.position = p + Vector3.up * 45f - fwd * 12f;
                transform.rotation = Quaternion.LookRotation(p - transform.position, fwd);
                break;
            case Mode.Driver:
                transform.position = p + Vector3.up * 1.55f + fwd * 0.7f;
                transform.rotation = Quaternion.LookRotation(fwd);
                break;
        }
    }
}
