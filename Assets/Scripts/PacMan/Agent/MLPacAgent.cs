using System;
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
    private int lastFoodCaried = 0;
    float distToBorder = 0f;
    int team => gameObject.CompareTag("Blue")? 1 : -1;
    public override void Initialize()
    {
        _agent = GetComponent<PacManAgentManager>();
        controller = GetComponent<PacManMovementController>();
        
    }
    public override void CollectObservations(VectorSensor sensor)
    {
        distToBorder = 0.3f - transform.position.x;
        distToBorder /= 12.5f; // Width of the map
        distToBorder = Mathf.Clamp(distToBorder, -1f, 1f);
        int hunterInt = _agent.isGhost? 1 : -1;
        
        // Our inputs are: team/side, distance to border (x = 0.3f), and whether or not we are hunted or hunter.
        sensor.AddObservation(hunterInt);
        sensor.AddObservation(distToBorder);
    }

    public void signalFoodDelivery(int nbr)
    {
        lastFoodCaried = 0;
        AddReward(1f * nbr);
    }

    public void signalKill()
    {
        AddReward(1f);
    }
    public void signalDeath()
    {
        AddReward(-1f*(lastFoodCaried+1));
        EndEpisode();
    }

    public void signalHitWall()
    {
        AddReward(-.005f);
        
    }
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        
        controller.ApplyDesiredControl(new Vector2(horizontal, vertical));
        
        if (lastFoodCaried < _agent.foodCarried.Count)
        {
            AddReward(1f);
            Debug.Log($"Reward sum: {GetCumulativeReward()}");
            lastFoodCaried = _agent.foodCarried.Count;
        }

        AddReward(_agent.GetScore()*.01f);
        Debug.Log($"Reward sum: {GetCumulativeReward()}");
    }

    public override void OnEpisodeBegin()
    {
        lastFoodCaried = 0;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        
        continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
        continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
    }
}
