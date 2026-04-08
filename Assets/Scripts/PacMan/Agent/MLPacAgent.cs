using System;
using System.Linq.Expressions;
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
    public int team => gameObject.CompareTag("Blue")? 1 : -1;
    private int previousScore = 0;
    private float foodScanRadius = 10f;
    private float clostestFood = 10f, lastClosestFood = 10f;
    private Vector3 prevPos;
    private int prevScore = 0;
    public override void Initialize()
    {
        _agent = GetComponent<PacManAgentManager>();
        controller = GetComponent<PacManMovementController>();
        prevPos = transform.position;
        prevScore = 0;
    }
    
    // Defined to be negative from ones own side and then positive on the other
    private float borderDistance(float x)
    {
        return Mathf.Clamp((x*team + .3f)/8, -1f, 1f);
    }
    
    public override void CollectObservations(VectorSensor sensor)
    {
        distToBorder = borderDistance(transform.position.x);
        int hunterInt = _agent.isGhost? 1 : -1;
        
        // Our inputs are: team/side, distance to border (x = 0.3f), and whether or not we are hunted or hunter. As well as direction we are looking.
        sensor.AddObservation(hunterInt);
        sensor.AddObservation(distToBorder);
        sensor.AddObservation(_agent.foodCarried.Count);
        
        //float timeRemaining = _agent != null? _agent.GetTimeRemaining() : 150f;
        //int score = _agent != null ? _agent.GetScore() : 0;
        ////if (_agent == null)
        //    Debug.Log("Agents is null");
        //sensor.AddObservation(timeRemaining);
        //sensor.AddObservation(score);
    }

    public void signalFoodDelivery(int nbr)
    {
        lastFoodCaried = 0;
        AddReward(1f * nbr);
        clostestFood = float.MaxValue;
        lastClosestFood = float.MaxValue; 
    }

    public void signalKill(int foodCarried)
    {
        AddReward(1.5f*(foodCarried+1));
    }
    public void signalDeath()
    {
        clostestFood = float.MaxValue;
        lastClosestFood = float.MaxValue; 
        AddReward(-1.5f*(lastFoodCaried+1));
        EndEpisode();
    }

    public void signalHitWall()
    {
        //AddReward(-.005f);
    }
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        
        controller.ApplyDesiredControl(new Vector2(team*horizontal, team*vertical));
        
        // Rewards collecting more food ============
        if (lastFoodCaried < _agent.foodCarried.Count)
        {
            AddReward(.65f);
            //Debug.Log($"Reward sum: {GetCumulativeReward()}");
            lastFoodCaried = _agent.foodCarried.Count;
            clostestFood = float.MaxValue;
            lastClosestFood = float.MaxValue; 
        }
        // Rewards moving closer to food =====================
        if (_agent.foodCarried.Count == 0)
        {
            float closest = float.MaxValue;
            foreach (GameObject foodObject in _agent.GetFoodObjects())
            {
                Vector3 objectPos = foodObject.transform.position;
                // Make sure food is on the other side
                if (borderDistance(objectPos.x) < 0.1f || Vector3.Distance(transform.position, objectPos) > closest)
                    continue;
                closest = Vector3.Distance(transform.position, objectPos);
            }
            clostestFood = closest;
            float diff = lastClosestFood - clostestFood;
            if (clostestFood < 1000f && lastClosestFood < 1000f && Mathf.Abs(diff) > .0001f)
            {
                AddReward(diff * .05f);
                //Debug.Log($"Getting closer to food, Reward sum: {GetCumulativeReward()}, increment: {diff*.05f}");
            }
            lastClosestFood = clostestFood;
        }
        
        // Rewards moving back to base with food ===================
        if (_agent.foodCarried.Count > 0)
        {
            float distToBase = borderDistance(transform.position.x);
            float prevDistToBase = borderDistance(prevPos.x);
            float diffToBase = prevDistToBase - distToBase;
            if (Mathf.Abs(diffToBase) > .0001f)
            {
                AddReward(0.15f * diffToBase * _agent.foodCarried.Count);
                //Debug.Log($"Reward for going closer to base with food, Reward sum: {GetCumulativeReward()},  increment: {0.05f * diffToBase * _agent.foodCarried.Count}");
            }
        }
        
        // Rewards for moving close to enemy pac-man
        foreach(var enemy in _agent.GetVisibleEnemyAgents())
        {
            if (enemy.isGhost || borderDistance(enemy.transform.position.x) > 0f
                                  || (!_agent.isGhost && borderDistance(enemy.transform.position.x) > -1.5f))
                continue;
            float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
            float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
            float distanceDiff = prevDistToEnemy - distToEnemy;
            if (distToEnemy < 4f && Mathf.Abs(distanceDiff) > .0001f)
            {
                AddReward(0.25f * distanceDiff);
                //Debug.Log($"Reward for being close to enemy as ghost, Reward sum: {GetCumulativeReward()}, increment: {0.1f * (0.5f - distToEnemy)}");
            }
        }
        prevPos =  transform.position;
    }

    public void OnGameEnd()
    {
        //if(_agent.GetScore() > 0)
        //{
        //    AddReward(10f);
        //}
        //else if (_agent.GetScore() == 0)
        //{
        //    AddReward(-5f);
        //}
        //else if(_agent.GetScore() < 0)
        //{
        //    AddReward(-10f);
        //}
        EndEpisode();
    }
    public override void OnEpisodeBegin()
    {
        clostestFood = float.MaxValue;
        lastClosestFood = float.MaxValue; 
        lastFoodCaried = 0;
        prevPos = transform.position; 
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        
        continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
        continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
    }
}
