using UnityEngine;

[RequireComponent(typeof(CarController))]
public class EngineAudio : MonoBehaviour
{
    public AudioClip hum, brake;
    public float volume = 0.5f;

    CarController car;
    AudioSource humSource, brakeSource;
    float brakeCooldown;

    void Start()
    {
        car = GetComponent<CarController>();
        humSource = gameObject.AddComponent<AudioSource>();
        humSource.clip = hum;
        humSource.loop = true;
        humSource.spatialBlend = 0.7f;
        humSource.volume = 0f;
        if (hum) humSource.Play();
        brakeSource = gameObject.AddComponent<AudioSource>();
        brakeSource.spatialBlend = 0.7f;
    }

    void Update()
    {
        float x = Mathf.Abs(car.Speed) / car.maxSpeed;
        humSource.pitch = 0.75f + 0.9f * x;
        humSource.volume = volume * (0.25f + 0.75f * x);
        brakeCooldown -= Time.deltaTime;
        if (brake && car.Accel < -5f && car.Speed > 5f && brakeCooldown <= 0f)
        {
            brakeSource.PlayOneShot(brake, volume);
            brakeCooldown = 2f;
        }
    }
}
