using Imported.StandardAssets.CrossPlatformInput.Scripts;
using PacMan.Game;
using PacMan.Local;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;


public class MLPacAgent: Agent
{
    private PacManMovementController controller;
    private PacManAgentManager _agent;
    public override void Initialize()
    {
        _agent = GetComponent<PacManAgentManager>();
        controller = GetComponent<PacManMovementController>();
    }


    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(controller.transform.position);
    }
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        
        controller.ApplyDesiredControl(new Vector2(horizontal, vertical));
    }

    public override void OnEpisodeBegin()
    {
        
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        
        continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
        continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
    }
}
