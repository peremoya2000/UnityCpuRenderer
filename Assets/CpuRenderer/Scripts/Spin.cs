using UnityEngine;

public class Spin : MonoBehaviour
{
    public Vector3 DegreesPerSecond;

    private void Update()
    {
        transform.rotation = Quaternion.Euler(Time.time * DegreesPerSecond);
    }
}
