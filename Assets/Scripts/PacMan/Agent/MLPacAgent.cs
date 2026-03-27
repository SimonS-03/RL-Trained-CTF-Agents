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
    private int team => gameObject.CompareTag("Blue")? 1 : -1;
    private int previousScore = 0;
    private float foodScanRadius = 10f;
    private float clostestFood = 10f;
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
        
        // Our inputs are: team/side, distance to border (x = 0.3f), and whether or not we are hunted or hunter. As well as direction we are looking.
        sensor.AddObservation(hunterInt);
        sensor.AddObservation(distToBorder);
        sensor.AddObservation(team);
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
            clostestFood = foodScanRadius;
        }

        Collider[] nearbyFood =
            Physics.OverlapSphere(transform.position, foodScanRadius, LayerMask.GetMask("Food"));

        
        foreach (Collider nearbyObject in nearbyFood)
        {
            Vector3 objectPos = nearbyObject.transform.position;
            if (objectPos.x < .3f*team || Vector3.Distance(transform.position, objectPos) > clostestFood)
                continue;
            clostestFood = Vector3.Distance(transform.position, objectPos);
        }
        
        //string teamString = gameObject.CompareTag("Blue") ? "Blue" : "Red"; 
        //Debug.Log($"Reward sum for {teamString} pac: {GetCumulativeReward()}. Score for its team is currently {_agent.GetScore()}");
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
