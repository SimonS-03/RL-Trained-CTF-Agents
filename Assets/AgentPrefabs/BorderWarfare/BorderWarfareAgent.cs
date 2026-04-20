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


public class BorderWarfareAgent: Unity.MLAgents.Agent
{
    private PacManMovementController controller;
    [SerializeField] private PacManAgentManager _agent;
    private Vector3 prevPos;
    private List<PacManAgentManager> allEnemies =  new List<PacManAgentManager>(),
        allFriendlies =  new List<PacManAgentManager>();

    [SerializeField] private AgentCoordinator coordinator;
    private bool isBlue;
    
    public override void Initialize()
    {
        controller = GetComponent<PacManMovementController>();
        _agent = GetComponent<PacManAgentManager>();
        prevPos = transform.position;
        isBlue = gameObject.CompareTag("Blue");

        coordinator = getCoordinator();
        if (coordinator == null)
            Debug.LogWarning("coordinator null in initialize!");
    }
    
    private AgentCoordinator getCoordinator()
    {
        AgentCoordinator coord = null;
        // 1. Check if we actually have a parent to avoid errors
        if (transform.parent != null)
        {
            // 2. Find the sibling by name, then get its component
            Transform managerObject = transform.parent.Find("TeamManager");
            
            if (managerObject != null)
            {
                coord = managerObject.GetComponent<AgentCoordinator>();
                if (coord == null)
                {
                    Debug.LogWarning("Coordinator not found within sibling!");
                }
            }
            else
            {
                Debug.LogWarning("Sibling not found!");
            }
        }
        else
        {
            Debug.LogWarning("No parent found!");
        }

        return coord;
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
        
        float distToBorder = coordinator.GetBorderFlowCost(other.transform.position, otherBlue);
        Vector3 directionToBorder = coordinator.GetBorderFlowDirection(other.transform.position, otherBlue);
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
            friendlyFood = isBlue ? coordinator.targetFoodBlue : coordinator.targetFoodRed;
            enemyFood = isBlue ? coordinator.targetFoodRed : coordinator.targetFoodBlue;
            capsules = coordinator.allCapsules;
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
    /// <param name="observations"></param>
    /// <returns></returns>
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
                worldPos = transform.parent.TransformPoint(obs.Position);
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
            
            float distToBorder = coordinator.GetBorderFlowCost(worldPos, !isBlue);
            Vector3 directionToBorder = coordinator.GetBorderFlowDirection(worldPos, !isBlue);
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
        
        if (coordinator == null)
        {
            coordinator = getCoordinator();
            if (coordinator == null)
                Debug.LogWarning("coordinator still null in collectObservations after trying to fetch!");
            else
            {
                Debug.Log("Coordinator found!");
            }
        }
        //Debug.Log("Visible count: " + visibleEnemies.Count + " hidden count: " + filteredObs.Count);
        
        // Here we only input the magnitude of velocity since we are always heading in our viewed direction
        float velMag = _agent.GetVelocity().magnitude / 3f; // Assuming 3 is about max speed
        sensor.AddObservation(velMag);
        sensor.AddObservation(Mathf.Clamp(_agent.GetScore()/10f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp01(_agent.foodCarried.Count/3f));
        
        // Border info
        float distToBorder = Mathf.Clamp01(coordinator.GetBorderFlowCost(transform.position, isBlue) / 6f);
        Vector3 directionToBorder = coordinator.GetBorderFlowDirection(transform.position, isBlue);
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
        if (allEnemies == null || allEnemies.Count == 0) // This is only used for reward, never inference!
        {
            allEnemies = _agent.PacManGameManager.GetComponentsInChildren<PacManAgentManager>()
                .Where(a => !gameObject.CompareTag(a.gameObject.tag)).ToList();
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
                LegalChaser legalChaser = friendly.GetComponent<LegalChaser>();
                BorderWarfareAgent bwAgent = friendly.GetComponent<BorderWarfareAgent>();
                if (legalChaser != null)
                {
                    legalChaser.lastHeard.Remove(otherAgent.serverIndex);
                }

                if (bwAgent != null)
                {
                    bwAgent.lastHeard.Remove(otherAgent.serverIndex);
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
    [SerializeField] private float  taggingOpponentReward = .1f, gettingTaggedReward = -.05f;
    [SerializeField] private float pickUpFoodReward = .05f, dropOffFoodReward = .1f;
    public void signalDropOffFood(int nbr)
    {
        AddReward(dropOffFoodReward * nbr);
    }
    public void signalPickUpFood()
    {
        AddReward(pickUpFoodReward);
    }
    
    public void signalKill()
    {
        //Debug.Log("Killed an agent");
        AddReward(taggingOpponentReward);
        //EndEpisode();
    }
    public void signalDeath()
    {
        AddReward(gettingTaggedReward);
        EndEpisode();
    }

    public void signalHitWall()
    {
        //AddReward(-.005f);
    }
    
    // Anti-stalling mechanism
    private Vector3 previousIdlePos =  Vector3.zero;
    private float idleTime = 0f;
    [SerializeField] private float idleStepPenalty = 0.025f;
    [SerializeField] private float idleGraceSeconds = 1f;
    [SerializeField] private float idleDistThreshold = 0.1f;
    [SerializeField] private float scoreTickPenalty = 0f;
    
    [SerializeField] private bool rewardGettingCloserToEnemy = false;
    [SerializeField] private bool rewardGettingFurtherFromEnemy = false;
    [SerializeField] private bool punishStalling = true;
    [SerializeField] private bool useScoreTickPenalty = true;
    [SerializeField] private bool useFriendlyProximityPenalty = true;
    
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        Vector3 acceleration = new Vector3(horizontal, 0f, vertical);
        acceleration = transform.TransformDirection(acceleration).normalized; // Convert from local to world space
        

        controller.ApplyDesiredControl(new Vector2(acceleration.x, acceleration.z));
        if (useScoreTickPenalty)
        {
            //Debug.Log($"Cumulative tick reward: {GetCumulativeReward()}");
            AddReward(scoreTickPenalty * Mathf.Clamp(_agent.GetScore() / 10f, -1f, 1f));
        }


        
        // Rewards for moving close to enemy pac-man ==============
        if (rewardGettingCloserToEnemy)
        {
            /*
            foreach(var enemy in _agent.GetVisibleEnemyAgents())
            {
                float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                float distanceDiff = prevDistToEnemy - distToEnemy;
                if (distToEnemy < 7.5f && Mathf.Abs(distanceDiff) > .0001f)
                {
                    AddReward(0.01f * distanceDiff);
                    //Debug.Log($"Reward for being close to enemy as ghost, Reward sum: {GetCumulativeReward()}, increment: {0.1f * (0.5f - distToEnemy)}");
                }
            }
            */
            
            float maxDistance = 7.5f;
            foreach(var enemy in allEnemies) // Loop through all agents of the opposite type (ghost vs pacman)
            {
                float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                
                // Calculate potential (higher is better). 
                // If outside 7.5f, potential is 0. If touching, potential is 1.
                float currentPotential = Mathf.Clamp01(1.0f - (distToEnemy / maxDistance));
                float prevPotential = Mathf.Clamp01(1.0f - (prevDistToEnemy / maxDistance));
                
                float potentialDifference = currentPotential - prevPotential; 

                if (Mathf.Abs(potentialDifference) > 0.0001f) 
                {
                    AddReward(potentialDifference * .3f);
                }
            }
            //Debug.Log("chaser enemies: " + nbr_enemies);
        }
        // Rewards moving away from enemy =================
        if (rewardGettingFurtherFromEnemy)
        {
            /*
            foreach(var enemy in _agent.GetVisibleEnemyAgents())
            {
                float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                float distanceDiff = distToEnemy - prevDistToEnemy;
                if (distToEnemy < 7.5f && Mathf.Abs(distanceDiff) > .0001f)
                {
                    AddReward(0.01f * distanceDiff);
                }
            }  
            */ 
            
            float maxDistance = 7.5f;
            foreach(var enemy in allEnemies)
            {
                float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                
                // Calculate potential (higher is better). 
                // If outside 7.5f, potential is 0. If touching, potential is 1.
                float currentPotential = Mathf.Clamp01(1.0f - (distToEnemy / maxDistance));
                float prevPotential = Mathf.Clamp01(1.0f - (prevDistToEnemy / maxDistance));
                
                float potentialDifference = currentPotential - prevPotential; 

                if (Mathf.Abs(potentialDifference) > 0.0001f) 
                {
                    AddReward(-potentialDifference * .3f);
                }
            }
            //Debug.Log("dodger enemies: " + nbr_enemies);
        }
        
        // Penalizes being stuck =================
        if (punishStalling)
        {
            float moved = Vector3.Distance(transform.position, previousIdlePos);
            if (moved < idleDistThreshold)
            {
                idleTime += Time.fixedDeltaTime;

                if (idleTime > idleGraceSeconds)
                {
                    AddReward(-idleStepPenalty * Time.fixedDeltaTime);
                }
            }
            else
            {
                previousIdlePos = transform.position;
                idleTime = 0f;
            }
        }
        
        
        // Social Distancing Penalty (Continuous Potential Field)
        float crowdRadius = 3.0f; // You can make this larger now since it fades out smoothly!
        float maxCrowdPenalty = 0.004f; // The penalty if they are standing exactly inside each other

        foreach (var friendly in allFriendlies) 
        {
            float distToFriend = Vector3.Distance(transform.position, friendly.transform.position);
    
            // Calculates how deep inside the radius they are. 
            // If distance is >= 5.0, this equals 0. If distance is 0, this equals 1.
            float crowdFactor = Mathf.Clamp01(1.0f - (distToFriend / crowdRadius));
    
            if (crowdFactor > 0.0001f) // Only penalize if they are actually inside the radius
            {
                // Multiply the max penalty by how close they are.
                // E.g., halfway inside the radius = half the penalty.
                AddReward(-crowdFactor * maxCrowdPenalty); 
            }
        }
        
        
        prevPos =  transform.position;
    }
    
    public override void OnEpisodeBegin()
    {
  
        prevPos = transform.position;
        idleTime = 0f;
        previousIdlePos = transform.position;
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
        
        if (hasDroppedOff)
            return;
        if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && gameObject.transform.localPosition.x > -0.3f ||
            TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && gameObject.transform.localPosition.x < 0.3f)
        {
            if (_agent.foodCarried.Count > 0)
            {
                signalDropOffFood(_agent.foodCarried.Count);
                //Debug.Log("Dropped of food");
                hasDroppedOff = true;
            }
        }
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
        idleTime = 0f;
        previousIdlePos = transform.position;
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
