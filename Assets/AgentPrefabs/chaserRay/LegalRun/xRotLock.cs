using UnityEngine;

public class xRotLock : MonoBehaviour
{
    void LateUpdate()
    {
        Vector3 rot = transform.rotation.eulerAngles;
        rot.x = 0f;
        transform.eulerAngles = rot;
    }
}