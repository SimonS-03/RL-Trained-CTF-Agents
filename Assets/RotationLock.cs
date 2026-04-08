using UnityEngine;

public class RotationLock : MonoBehaviour
{
    MLPacAgent _ml_agent;

    private void Start()
    {
        _ml_agent = GetComponentInParent<MLPacAgent>();
    }

    void LateUpdate()
    {
        if (_ml_agent == null)
        {
            _ml_agent = GetComponentInParent<MLPacAgent>();
            return;
        }
        //Debug.Log($"Team is {_ml_agent.team}");
        // Lock to world-space +X (team 1) or -X (others) on the XZ plane.
        Vector3 worldFacing = _ml_agent.team == 1 ? Vector3.right : Vector3.left;
        transform.rotation = Quaternion.LookRotation(worldFacing, Vector3.up);
    }
}
