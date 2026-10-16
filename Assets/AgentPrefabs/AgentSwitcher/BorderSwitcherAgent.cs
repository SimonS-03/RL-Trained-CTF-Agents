using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using Imported.StandardAssets.CrossPlatformInput.Scripts;
using JetBrains.Annotations;
using PacMan;
using PacMan.Agent;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using PacMan.RLAgent;
using Scripts.Map;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;


public class BorderSwitcherAgent: Unity.MLAgents.Agent
{
    private PacManMovementController controller;
    [SerializeField] private PacManAgentManager _agent;
    private Vector3 prevPos;

    private List<PacManAgentManager> allFriendlies;

    private AgentCoordinator ourCoordinator, theirCoordinator;
    private bool isBlue;
    private Switcher _switcher;
    public override void Initialize()
    {
        _switcher = GetComponentInParent<Switcher>();
        _agent = GetComponentInParent<PacManAgentManager>();
        prevPos = transform.position;
        isBlue = transform.parent.gameObject.CompareTag("Blue");
        
        ourCoordinator = isBlue ? AgentCoordinator.BlueInstance : AgentCoordinator.RedInstance;
        theirCoordinator = isBlue ? AgentCoordinator.RedInstance : AgentCoordinator.BlueInstance;
    }
    
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        Vector3 acceleration = new Vector3(horizontal, 0f, vertical);
        acceleration = transform.TransformDirection(acceleration).normalized; // Convert from local to world space
        if (_switcher != null)
        {
            _switcher.ReceiveActionFromAgent(new PacManAction
            {
                Acceleration = new Vector2(acceleration.x, acceleration.z)
            });
        }
        else
        {
            Debug.LogWarning("Switcher component not found on agent. Actions will not be applied.");
            _switcher = GetComponentInParent<Switcher>();
        }
        prevPos =  transform.position;
    }
    
    private const int AgentObservationSize = 13; // size of an agent observation
    private const int ObjectObservationSize = 9; // size of an object observation (food or capsule)
    private float[] BuildRealAgentObservation(PacManAgentManager other, int isReal = 1)
    {
        Vector3 rawDiffPos = other.transform.position - _agent.transform.position;
        Vector3 direction = transform.InverseTransformDirection(rawDiffPos.normalized); 
        float distance = rawDiffPos.magnitude;
        float normalizedDistance = Mathf.Clamp(distance / 12.5f, 0f, 1f);
        
        Vector3 relativeVel = transform.InverseTransformDirection(other.GetVelocity() - _agent.GetVelocity());
    
        // Note: If max speed is 3, max RELATIVE speed is 6 (crashing head-on). 
        // Divide by 6f to keep it properly between -1 and 1.
        Vector3 normalizedRelVel = relativeVel / 6f;
        float isVisible = 1f;
        float normalizedFood = Mathf.Clamp01(other.foodCarried.Count / 3f);

        bool otherBlue = other.gameObject.CompareTag("Blue");
        AgentCoordinator theirCoordinator = otherBlue? AgentCoordinator.BlueInstance : AgentCoordinator.RedInstance;
        
        float distToBorder = theirCoordinator.GetBorderFlowCost(other.transform.position);
        float otherDistToBorder = ourCoordinator.GetBorderFlowCost(other.transform.position);
        Vector3 directionToBorder;
        if (distToBorder > otherDistToBorder)
        {
            directionToBorder = theirCoordinator.GetBorderFlowDirection(other.transform.position);
        }
        else
        {
            distToBorder = otherDistToBorder;
           directionToBorder = ourCoordinator.GetBorderFlowDirection(other.transform.position);
        }
        
        if (distToBorder > 1000f) // If the position is in an obstacle/not defined
        {
            distToBorder = Mathf.Abs(other.transform.position.x); // Assuming border is pretty much at x = 0
            directionToBorder = other.transform.position.x < 0f ? Vector3.right : Vector3.left;
        } 
        distToBorder = Mathf.Clamp01(distToBorder/6f);
        directionToBorder = transform.InverseTransformDirection(directionToBorder).normalized;
        
        return new[]
        {
            direction.x,                     
            direction.z,                     
            normalizedDistance,              
            Mathf.Clamp(normalizedRelVel.x, -1f, 1f),
            Mathf.Clamp(normalizedRelVel.z, -1f, 1f),
            normalizedFood,
            distToBorder,
            directionToBorder.x,
            directionToBorder.z,
            other.isGhost? 1 : -1,
            other.isScared? 1 : -1,
            isVisible,
            isReal,
        };
    }

    private float[] buildObjectObservation(Vector3 pos, bool food, bool capsule)
    {
        Vector3 rawDiffPos = pos - _agent.transform.position;
        Vector3 direction = transform.InverseTransformDirection(rawDiffPos.normalized); 
        float distance = rawDiffPos.magnitude;
        float normalizedDistance = Mathf.Clamp(distance / 12.5f, 0f, 1f);
        
        Vector3 relativeVel = transform.InverseTransformDirection(Vector3.zero - _agent.GetVelocity());
    
        // Strictly speaking, we might normalize by a factor of 3f instead of 6f since food/capsules don't move,
        // but we might as well keep the same normalization for consistency.
        Vector3 normalizedRelVel = relativeVel / 6f;
        
        float isFood = food? 1f : 0f;
        float isCapsule = capsule? 1f : 0f;
        float onOwnSide;

        if (isBlue)
        {
            onOwnSide = pos.x < 0f ? 1f : -1f;
        }
        else
        {
            onOwnSide = pos.x > 0f ? 1f : -1f;
        }

        float isReal = 1f;
        return new []        {
            direction.x,
            direction.z,
            normalizedDistance,
            Mathf.Clamp(normalizedRelVel.x, -1f, 1f),
            Mathf.Clamp(normalizedRelVel.z, -1f, 1f),
            onOwnSide,
            isFood,
            isCapsule,
            isReal
        };


    }
    
    private int AppendFixedSizeObjectBuffer(VectorSensor sensor, int friendlyFoodNumber = 4, int enemyFoodNumber = 4, int capsuleNumber = 4)
    {
        int totalWantedObservations = friendlyFoodNumber + enemyFoodNumber + capsuleNumber;
        List<Vector3> friendlyFood;
        List<Vector3> enemyFood;
        List<Vector3> capsules;
        try
        {
            friendlyFood = theirCoordinator.targetFood;
            enemyFood = ourCoordinator.targetFood;
            capsules = ourCoordinator.allCapsules;
        }
        catch
        {
            Debug.Log("Cant fetch food or capsule lists from agentCoordinator!");
            for (int i = 0; i < totalWantedObservations; i++)
            {
                sensor.AddObservation(new float[ObjectObservationSize]);
            }
            return 0;
        }
        
        // sorting by distance
        friendlyFood = friendlyFood.OrderBy(f => Vector3.Distance(transform.position, f)).Take(friendlyFoodNumber).ToList();
        enemyFood = enemyFood.OrderBy(f => Vector3.Distance(transform.position, f)).Take(enemyFoodNumber).ToList();
        
        List<Vector3> allFood = friendlyFood.Concat(enemyFood).OrderBy(o => Vector3.Distance(transform.position, o)).ToList();
        capsules = capsules.OrderBy(f => Vector3.Distance(transform.position, f)).Take(capsuleNumber).ToList();
        
        
        // Adding food buffer
        for (int i = 0; i < allFood.Count; i++)
        {
            sensor.AddObservation(buildObjectObservation(allFood[i], true, false));
        }
        int foodRemaining = friendlyFoodNumber + enemyFoodNumber - allFood.Count;
        for (int i = 0; i < foodRemaining; i++)
        {
            sensor.AddObservation(new float[ObjectObservationSize]);
        }
        
        // Adding capsule buffer
        for (int i = 0; i < capsules.Count; i++)
        {
            sensor.AddObservation(buildObjectObservation(capsules[i], false, true));
        }
        int capsulesRemaining = capsuleNumber - capsules.Count;
        for (int i = 0; i < capsulesRemaining; i++)
        {
            sensor.AddObservation(new float[ObjectObservationSize]);
        }
        
        
        int objectCount = friendlyFood.Count + enemyFood.Count + capsules.Count;
        return objectCount;
    }
    
    
    private int AppendFixedSizeAgentBuffer(VectorSensor sensor, List<PacManAgentManager> entities, List<float[]> observations, int fixedNbrEntities)
    {
        List<float[]> enemyArrays = entities.Select(e => BuildRealAgentObservation(e)).ToList(); ;
        if (observations != null)
        {
            enemyArrays.AddRange(observations);
        }
        enemyArrays = enemyArrays.OrderBy(e => e[2]).ToList();

        int realCount = enemyArrays.Count;
        for (int i = 0; i < realCount; i++)
        {
            sensor.AddObservation(enemyArrays[i]);
        }
        
        // If we have less than 4 agents, seen or unseen.
        for (int i = realCount; i < fixedNbrEntities; i++)
        {
            sensor.AddObservation(new float[AgentObservationSize]);
        }

        return realCount;
    }
    public Dictionary<int, Tuple<Vector3, float>> lastHeard = new Dictionary<int, Tuple<Vector3, float>>(); // serverIndex to worldPosition (localPosition?) and food carried
    /// <summary>
    /// Gets a list of obervations from pacManObservations that has filtered out visible enemies
    /// </summary>
    public List<float[]> filterAgentObservation(PacManObservations observations, List<PacManAgentManager> visibleEnemies)
    {
        List<int> visibleIndices = visibleEnemies.Select(a => a.serverIndex).ToList();

        foreach (var obs in visibleEnemies)
        {
            lastHeard[obs.serverIndex] = Tuple.Create(obs.transform.position, Mathf.Clamp01(obs.foodCarried.Count/3f));
        }
        
        List<float[]> filteredObservations = new List<float[]>();
        foreach (PacManObservation obs in observations.Observations)
        {
            if (visibleIndices.Contains(obs.ServerIndex))
                continue;
            
            Vector3 worldPos;
            bool knowOfPrevFood = lastHeard.ContainsKey(obs.ServerIndex) && lastHeard[obs.ServerIndex].Item2 > 0.01f;
            float normalizedFood = knowOfPrevFood? lastHeard[obs.ServerIndex].Item2 : 
                (obs.HasFood? 0.33f : 0f); 
            
            if (Vector3.Distance(Vector3.zero, obs.Position) < .001f) // If the agent is not making a sound
            {
                if (lastHeard.ContainsKey(obs.ServerIndex))
                {
                    //Debug.Log("Used remembered observation!");
                    worldPos = lastHeard[obs.ServerIndex].Item1;
                    //Debug.Log($"Used remembered observation:  {worldPos}");
                }
                else
                {
                    continue; // If we have never heard this agent, we have no information about it, so skip the observation.
                }   
            }
            else
            {
                worldPos = transform.parent.parent.TransformPoint(obs.Position);
                lastHeard[obs.ServerIndex] = Tuple.Create(worldPos, normalizedFood);
            }
            
            
            //Debug.Log(obs.Position);
            
            Vector3 rawDiffPos = worldPos - _agent.transform.position;
            Vector3 direction = transform.InverseTransformDirection(rawDiffPos.normalized); 
            float distance = rawDiffPos.magnitude;
            float normalizedDistance = Mathf.Clamp01(distance / 12.5f);
        
            // Assuming observations do not have a velocity 
            Vector3 relativeVel = transform.InverseTransformDirection(Vector3.zero - _agent.GetVelocity());
            Vector3 normalizedRelVel = relativeVel / 6f; 
            
            float distToBorder = theirCoordinator.GetBorderFlowCost(worldPos);
            float otherDistToBorder = ourCoordinator.GetBorderFlowCost(worldPos);
            Vector3 directionToBorder;
            if (distToBorder > otherDistToBorder)
            {
                directionToBorder = theirCoordinator.GetBorderFlowDirection(worldPos);
            }
            else
            {
                distToBorder = otherDistToBorder;
                directionToBorder = ourCoordinator.GetBorderFlowDirection(worldPos);
            }
            
            
            if (distToBorder > 1000f) // If the position is in an obstacle/not defined
            {
                distToBorder = Mathf.Abs(worldPos.x); // Assuming border is pretty much at x = 0
                directionToBorder = worldPos.x < 0f ? Vector3.right : Vector3.left;
            } 
            
            distToBorder = Mathf.Clamp01(distToBorder/6f);
            directionToBorder = transform.InverseTransformDirection(directionToBorder).normalized;

            float isScared = -1f;
            if (visibleEnemies.Count > 0 && visibleEnemies[0].isScared)
                isScared = 1f;
            
            float[] newObs = new []
            {
                direction.x,
                direction.z,
                normalizedDistance,
                Mathf.Clamp(normalizedRelVel.x, -1f, 1f),
                Mathf.Clamp(normalizedRelVel.z, -1f, 1f),
                normalizedFood,
                distToBorder,
                directionToBorder.x,
                directionToBorder.z,
                obs.IsGhost? 1f : -1f,
                isScared, // assuming they are not scared?
                0f, // Not visible
                1f // But a real observation
            };
            
            //Debug.Log(newObs[2]);
            filteredObservations.Add(newObs);
        }

        return filteredObservations;
    }
    
    public override void CollectObservations(VectorSensor sensor)
    {
        
        //Debug.Log("Visible count: " + visibleEnemies.Count + " hidden count: " + filteredObs.Count);
        
        // Here we only input the magnitude of velocity since we are always heading in our viewed direction
        float velMag = _agent.GetVelocity().magnitude / 3f; // Assuming 3 is about max speed
        sensor.AddObservation(velMag);
        sensor.AddObservation(Mathf.Clamp(_agent.GetScore()/10f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp01(_agent.foodCarried.Count/3f));
        
        // Border info
        //float distToBorder = Mathf.Clamp01(ourCoordinator.GetBorderFlowCost(transform.position) / 6f);
        //Vector3 directionToBorder = ourCoordinator.GetBorderFlowDirection(transform.position);
        //directionToBorder = transform.InverseTransformDirection(directionToBorder).normalized;
        
        float distToBorder = theirCoordinator.GetBorderFlowCost(transform.position);
        float otherDistToBorder = ourCoordinator.GetBorderFlowCost(transform.position);
        Vector3 directionToBorder;
        if (distToBorder > otherDistToBorder)
        {
            directionToBorder = theirCoordinator.GetBorderFlowDirection(transform.position);
        }
        else
        {
            distToBorder = otherDistToBorder;
            directionToBorder = ourCoordinator.GetBorderFlowDirection(transform.position);
        }
        distToBorder = Mathf.Clamp01(distToBorder/6f);
        directionToBorder = transform.InverseTransformDirection(directionToBorder).normalized;
        
        sensor.AddObservation(distToBorder);
        sensor.AddObservation(directionToBorder.x);
        sensor.AddObservation(directionToBorder.z);
        sensor.AddObservation(isBlue? 1 : -1);
        sensor.AddObservation(_agent.isGhost? 1 : -1);
        sensor.AddObservation(_agent.isScared? 1 : -1);
        
        var observations = _agent.GetEnemyObservations();
        List<PacManAgentManager> visibleEnemies = _agent.GetVisibleEnemyAgents(); 
        var filteredObs = filterAgentObservation(observations, visibleEnemies);

        //Debug.Log(visibleEnemies.Count);
        //Debug.Log(friendlies.Count);
        if (allFriendlies == null) // This is only used for reward, never inference!
        {
            allFriendlies = _agent.GetFriendlyAgents();
        }
        
        int enemyBufferSize = AppendFixedSizeAgentBuffer(sensor, visibleEnemies, filteredObs, 4);
        int friendlyBufferSize = AppendFixedSizeAgentBuffer(sensor, allFriendlies, null, 3);
        int objectsRegistered = AppendFixedSizeObjectBuffer(sensor);
    }
    
    public virtual void OnCollisionStay(Collision other)
    {
        var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
        if (otherAgent != null && other.gameObject.tag != tag && !otherAgent.isScared && (!_agent.IsGhost() && otherAgent.IsGhost() ||
                _agent.IsGhost() && otherAgent.IsGhost() ||
                !_agent.IsGhost() && !otherAgent.IsGhost()))
        {
            signalDeath();
        }

        if (otherAgent != null && other.gameObject.tag != tag && _agent.IsGhost() && _agent.isScared && !otherAgent.IsGhost())
        {
            signalDeath();
        }
        
        if (otherAgent != null && other.gameObject.tag != tag && _agent.IsGhost() && !_agent.isScared && !otherAgent.IsGhost())
        {
            // Remove from memory so that we don't keep "remembering" a dead agent's position
            lastHeard.Remove(otherAgent.serverIndex); 
            foreach (PacManAgentManager friendly in _agent.GetFriendlyAgents())
            {
                SwitcherAgent chaseSwitcher = friendly.GetComponentInChildren<SwitcherAgent>();
                BorderSwitcherAgent borderSwitcher = friendly.GetComponentInChildren<BorderSwitcherAgent>();
                if (chaseSwitcher != null)
                {
                    chaseSwitcher.lastHeard.Remove(otherAgent.serverIndex);
                }
                if (borderSwitcher != null)
                {
                    borderSwitcher.lastHeard.Remove(otherAgent.serverIndex);
                }
            }
            signalKill();
        }
    }

    public virtual void OnTriggerEnter(Collider other)
    {
        if (!_agent.IsGhost() && other.gameObject.name == "Food" && TeamAssignmentUtil.CheckTeam(other.gameObject) != TeamAssignmentUtil.CheckTeam(gameObject))
        {
            //Debug.Log("Picked up food!");
            hasDroppedOff = false;
            signalPickUpFood();
        }
    }

    private bool hasDroppedOff = false;

    
    // Sparse rewards
    //[SerializeField] private float  taggingOpponentReward = .1f, gettingTaggedReward = -.05f;
    //[SerializeField] private float pickUpFoodReward = .05f, dropOffFoodReward = .1f;
    public void signalDropOffFood(int nbr)
    {
    }
    public void signalPickUpFood()
    {
    }
    
    public void signalKill()
    {
    }
    public void signalDeath()
    {
        EndEpisode();
    }

    public void signalHitWall()
    {
    }
    
    
    public override void OnEpisodeBegin()
    {
        prevPos = transform.position;
    }

    private ObstacleMap obstacleMap;

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        
        continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
        continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
    }
    
    
    private int _lastStep = -1;
    private float _lastTime = -1f;
    private bool _seenFirstTick = false;
    private static bool _restartLoggedForCurrentResetWindow = false;

    private void FixedUpdate()
    {
        TryDetectGameRestart();
    }

    private void TryDetectGameRestart()
    {
        if (_agent == null || _agent.PacManGameManager == null) return;

        int step = _agent.PacManGameManager.CurrentSimulationStep;
        float simTime = _agent.PacManGameManager.CurrentSimulationTime;

        // Re-arm logging only after the new match has clearly progressed past reset ticks.
        if (step > 1 && simTime > Time.fixedDeltaTime)
        {
            _restartLoggedForCurrentResetWindow = false;
        }

        if (!_seenFirstTick)
        {
            _lastStep = step;
            _lastTime = simTime;
            _seenFirstTick = true;
            return;
        }

        // Only treat rollbacks near t=0 as a restart to avoid duplicate triggers on early post-reset ticks.
        bool rolledBack = (step < _lastStep) || (simTime < _lastTime);
        bool nearResetWindow = (step <= 1) || (simTime <= Time.fixedDeltaTime + 0.0001f);
        bool restarted = rolledBack && nearResetWindow;

        if (restarted)
        {
            OnGameRestartDetected();
        }

        _lastStep = step;
        _lastTime = simTime;
    }

    private void OnGameRestartDetected()
    {
        // Put your restart-specific logic here:
        lastHeard.Clear();
        hasDroppedOff = false;
        prevPos = transform.position;

        // Shared static gate so only one agent instance logs per restart.
        if (!_restartLoggedForCurrentResetWindow)
        {
            _restartLoggedForCurrentResetWindow = true;
        }
        //Debug.Log("Detected game restart!");
        // Optional but recommended for ML-Agents episode alignment:
        EndEpisode();
    }
}
