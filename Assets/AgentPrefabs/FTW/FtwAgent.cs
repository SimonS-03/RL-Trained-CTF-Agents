using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Imported.StandardAssets.CrossPlatformInput.Scripts;
using PacMan.Game;
using PacMan.Local;
using Scripts.Map;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;


public class FtwAgent: Unity.MLAgents.Agent
{
    private const int MaxEntitiesPerBuffer = 4;
    private const int EntityObservationSize = 4;

    private PacManMovementController controller;
    [SerializeField] private PacManAgentManager _agent;
    
    [SerializeField] private BufferSensorComponent _enemyBufferSensor;
    [SerializeField] private BufferSensorComponent _friendlyBufferSensor;
    
    private int lastFoodCaried = 0;
    float distToBorder = 0f;
    public int team => gameObject.CompareTag("Blue")? 1 : -1;
    private float clostestFood = 10f, lastClosestFood = 10f;
    private Vector3 prevPos;
    private int prevScore = 0;
    public int teamMateIndex = -1;
    
    
    private float _wCapture = 1f, _wPickup = 1f, _wTaggingOpponent = 1f, _wGettingTagged = 1f;
    
    public override void Initialize()
    {
        controller = GetComponent<PacManMovementController>();
        if (_agent == null)
        {
            _agent = GetComponent<PacManAgentManager>();
        }
        
        prevPos = transform.position;
        prevScore = 0;
    }
    
    // Defined to be negative from ones own side and then positive on the other
    private float borderDistance(float x)
    {
        return Mathf.Clamp((x*team + .3f)/8f, -1f, 1f);
    }

    private float[] BuildEntityObservation(PacManAgentManager other)
    {
        Vector3 diffPos = other.transform.position - _agent.transform.position;
        return new[]
        {
            Mathf.Clamp(diffPos.x * team / 12f, -1f, 1f),
            Mathf.Clamp(diffPos.z * team / 12f, -1f, 1f),
            other.isGhost ? 1f : -1f,
            Mathf.Clamp01(other.foodCarried.Count / 3f)
        };
    }

    private int AppendFixedSizeEntityBuffer(BufferSensorComponent sensor, List<PacManAgentManager> entities)
    {
        int realCount = Mathf.Min(entities.Count, MaxEntitiesPerBuffer);

        for (int i = 0; i < realCount; i++)
        {
            sensor.AppendObservation(BuildEntityObservation(entities[i]));
        }

        // Always write exactly MaxEntitiesPerBuffer entries.
        for (int i = realCount; i < MaxEntitiesPerBuffer; i++)
        {
            sensor.AppendObservation(new float[EntityObservationSize]);
        }

        return realCount;
    }
    
    public override void CollectObservations(VectorSensor sensor)
    {
        distToBorder = borderDistance(transform.position.x);
        int hunterInt = _agent.isGhost? 1 : -1;
        
        // Our inputs are: team/side, distance to border (x = 0.3f), and whether or not we are hunted or hunter. As well as direction we are looking.
        
        sensor.AddObservation(hunterInt);
        sensor.AddObservation(Mathf.Clamp(distToBorder/12f, -1f, 1f));
        //sensor.AddObservation(teamMateIndex);
        if (_agent != null)
        {
            sensor.AddObservation(Mathf.Clamp01(_agent.foodCarried.Count/3f));
            sensor.AddObservation(Mathf.Clamp(_agent.GetScore()/10f, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp01(_agent.GetTimeRemaining()/150f));
        }
        else
        {
            sensor.AddObservation(1f);
            sensor.AddObservation(1f);
            sensor.AddObservation(1f);
            Debug.Log("Agent is null");
        }
        
        // Reward parameters, the agent needs to know to play smartly!
        sensor.AddObservation(_wCapture);
        sensor.AddObservation(_wPickup);
        sensor.AddObservation(_wTaggingOpponent);
        sensor.AddObservation(_wGettingTagged);

        
        // Deep sets for detecting relative enemy and friendly positions!
        List<PacManAgentManager> visibleEnemies =  new List<PacManAgentManager>();
        List<PacManAgentManager> friendlies =  new List<PacManAgentManager>();
        List<Vector3> foodPositions = new List<Vector3>();
        try
        {
            visibleEnemies = _agent.GetVisibleEnemyAgents();
            friendlies = _agent.GetFriendlyAgents();
        }
        catch (Exception e)
        {
            Debug.LogWarning(e);
        }

        int enemyBufferSize = AppendFixedSizeEntityBuffer(_enemyBufferSensor, visibleEnemies);
        int friendlyBufferSize = AppendFixedSizeEntityBuffer(_friendlyBufferSensor, friendlies);
        
        
        Debug.Log($"nbr friendlies visible:  {friendlyBufferSize}. Enemies: {enemyBufferSize}");
    }

    public void signalFoodDelivery(int nbr)
    {
        lastFoodCaried = 0;
        AddReward(.05f*nbr * _wCapture);
        clostestFood = float.MaxValue;
        lastClosestFood = float.MaxValue;
    }

    public void signalKill(int foodCarried)
    {
        //Debug.Log($"Kill weight: {weight}");
        AddReward(.025f*(foodCarried+1) * _wTaggingOpponent);
    }
    public void signalDeath()
    {
        //Debug.Log($"Death weight with {gameObject.name} and index {teamMateIndex}: {_wGettingTagged}");
        AddReward(-.05f*(lastFoodCaried+1) * _wGettingTagged);
        clostestFood = float.MaxValue;
        lastClosestFood = float.MaxValue;
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
            AddReward(.025f*_wPickup);

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
                AddReward(diff * .005f * _wPickup);
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
                AddReward(0.005f * diffToBase * _agent.foodCarried.Count * _wCapture);
                //Debug.Log($"Reward for going closer to base with food, Reward sum: {GetCumulativeReward()},  increment: {0.05f * diffToBase * _agent.foodCarried.Count}");
            }
        }
        
        // Rewards for moving close to enemy pac-man
        foreach(var enemy in _agent.GetVisibleEnemyAgents())
        {
            if (enemy.isGhost || borderDistance(enemy.transform.position.x) > 0f
                                  || (!_agent.isGhost && borderDistance(enemy.transform.position.x) > -1f))
                continue;
            float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
            float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
            float distanceDiff = prevDistToEnemy - distToEnemy;
            if (distToEnemy < 4f && Mathf.Abs(distanceDiff) > .0001f)
            {
                AddReward(0.005f * distanceDiff * _wTaggingOpponent);
                //Debug.Log($"Reward for being close to enemy as ghost, Reward sum: {GetCumulativeReward()}, increment: {0.1f * (0.5f - distToEnemy)}");
            }
        }
        // Rewards moving away from enemy ghost
        foreach(var enemy in _agent.GetVisibleEnemyAgents())
        {
            if (!enemy.isGhost || _agent.isGhost)
                continue;
            float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
            float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
            float distanceDiff = distToEnemy - prevDistToEnemy;
            if (distToEnemy < 3.5f && Mathf.Abs(distanceDiff) > .0001f)
            {
                AddReward(0.005f * distanceDiff * _wGettingTagged);
            }
        }

        prevPos =  transform.position;
    }
    
    public override void OnEpisodeBegin()
    {
        var p = Academy.Instance.EnvironmentParameters;
        _wCapture = p.GetWithDefault("flag_capture_weight", 1.0f);
        _wPickup = p.GetWithDefault("flag_pickup_weight", 1.0f);
        _wTaggingOpponent = p.GetWithDefault("tagging_opponent_weight", 1.0f);
        _wGettingTagged = p.GetWithDefault("getting_tagged_weight", 1.0f);
        //Debug.Log($"New episode begun for {gameObject.name} (index {teamMateIndex}) " +
        //          $"with weights: capture {_wCapture}, pickup {_wPickup}, " +
        //          $"tagging opponent {_wTaggingOpponent}, getting tagged {_wGettingTagged}");
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
