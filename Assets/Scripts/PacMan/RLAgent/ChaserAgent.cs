using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Imported.StandardAssets.CrossPlatformInput.Scripts;
using PacMan;
using PacMan.Agent;
using PacMan.Game;
using PacMan.Local;
using PacMan.RLAgent;
using Scripts.Map;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

namespace PacMan.RLAgent
{
    public class ChaserAgent: Unity.MLAgents.Agent
    {
        private PacManMovementController controller;
        [SerializeField] private PacManAgentManager _agent;
        private Vector3 prevPos;
        
        public override void Initialize()
        {
            controller = GetComponent<PacManMovementController>();
            _agent = GetComponent<PacManAgentManager>();
            prevPos = transform.position;
        }
        
        
        private const int EntityObservationSize = 5; // size of an agent observation
        private float[] BuildEntityObservation(PacManAgentManager other, int isReal = 1)
        {
            Vector3 diffPos = (other.transform.position - _agent.transform.position)/12f;
            Vector3 otherVel = other.GetVelocity()/3f;
            //Debug.Log("pos: " + diffPos);
            //Debug.Log("vel: " + otherVel);
            return new[]
            {
                Mathf.Clamp(diffPos.x, -1f, 1f),
                Mathf.Clamp(diffPos.z, -1f, 1f),
                Mathf.Clamp(otherVel.x, -1f, 1f),
                Mathf.Clamp(otherVel.z, -1f, 1f),
                isReal
            };
        }

        private int AppendFixedSizeEntityBuffer(VectorSensor sensor, List<PacManAgentManager> entities, int fixedNbrEntities)
        {
            int realCount = Mathf.Min(entities.Count, fixedNbrEntities);

            for (int i = 0; i < realCount; i++)
            {
                sensor.AddObservation(BuildEntityObservation(entities[i]));
            }

            // Always write exactly MaxEntitiesPerBuffer entries.
            for (int i = realCount; i < fixedNbrEntities; i++)
            {
                sensor.AddObservation(new float[EntityObservationSize]);
            }

            return realCount;
        }
        
        public override void CollectObservations(VectorSensor sensor)
        {
            // Deep sets for detecting relative enemy and friendly positions!
            List<PacManAgentManager> visibleEnemies =  new List<PacManAgentManager>();
            List<PacManAgentManager> friendlies =  new List<PacManAgentManager>();
            try
            {
                visibleEnemies = _agent.GetVisibleEnemyAgents();
                friendlies = _agent.GetFriendlyAgents();
            }
            catch (Exception e)
            {
                Debug.LogWarning(e);
            }
            //Debug.Log(visibleEnemies.Count);
            //Debug.Log(friendlies.Count);
            int enemyBufferSize = AppendFixedSizeEntityBuffer(sensor, visibleEnemies, 4);
            int friendlyBufferSize = AppendFixedSizeEntityBuffer(sensor, friendlies, 3);
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
                //tagged other
                signalKill();
            }
        }
        
        public void signalKill()
        {
            Debug.Log("Killed an agent");
            AddReward(taggingOpponentReward);
            EndEpisode();
        }
        public void signalDeath()
        {
            Debug.Log("Died...");
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
        [SerializeField] private float tickPenalty = -.0005f;
        
        [SerializeField] private bool rewardGettingCloserToEnemy = true;
        [SerializeField] private bool rewardGettingFurtherFromEnemy = true;
        [SerializeField] private bool punishStalling = true;
        [SerializeField] private bool useTickPenalty = true;
        
        // Sparse rewards
        [SerializeField] private float  taggingOpponentReward = 1f, gettingTaggedReward = -1f;
        public override void OnActionReceived(ActionBuffers actions)
        {
            float horizontal = actions.ContinuousActions[0];
            float vertical = actions.ContinuousActions[1];
            
            controller.ApplyDesiredControl(new Vector2(horizontal, vertical));

            if (useTickPenalty)
            {
                //Debug.Log($"Cumulative tick reward: {GetCumulativeReward()}");
                AddReward(tickPenalty);
            }
                
            
            // Rewards for moving close to enemy pac-man ==============
            if (rewardGettingCloserToEnemy)
            {
                foreach(var enemy in _agent.GetVisibleEnemyAgents())
                {
                    float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                    float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                    float distanceDiff = prevDistToEnemy - distToEnemy;
                    if (distToEnemy < 7.5f && Mathf.Abs(distanceDiff) > .0001f)
                    {
                        AddReward(0.005f * distanceDiff);
                        //Debug.Log($"Reward for being close to enemy as ghost, Reward sum: {GetCumulativeReward()}, increment: {0.1f * (0.5f - distToEnemy)}");
                    }
                }
            }
            // Rewards moving away from enemy =================
            if (rewardGettingFurtherFromEnemy)
            {
                foreach(var enemy in _agent.GetVisibleEnemyAgents())
                {
                    float distToEnemy = Vector3.Distance(transform.position, enemy.transform.position);
                    float prevDistToEnemy = Vector3.Distance(prevPos, enemy.transform.position);
                    float distanceDiff = distToEnemy - prevDistToEnemy;
                    if (distToEnemy < 5f && Mathf.Abs(distanceDiff) > .0001f)
                    {
                        AddReward(0.005f * distanceDiff);
                    }
                }    
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
            
            prevPos =  transform.position;
        }

        [SerializeField] private bool respawnOnRandomPosition = false;
        public override void OnEpisodeBegin()
        {
            //Debug.Log("Episode Begin");
            if (respawnOnRandomPosition)
                transform.position = getRandomMapPosition();
            
            prevPos = transform.position;
            idleTime = 0f;
            previousIdlePos = transform.position;
        }

        private ObstacleMap obstacleMap;
        private Vector3 getRandomMapPosition()
        {
            if (_agent.PacManGameManager == null)
            {
                Debug.LogWarning("No obstacle map found");
                return transform.position;
            }
            if (obstacleMap == null)
            {
                obstacleMap = ObstacleMap.Initialize(
                    _agent.PacManGameManager.mapManager,
                    new List<GameObject>(),
                    Vector3.one,
                    Vector3.one);
                Debug.Log("Initialized obstacle map");
            }
                
            
            Vector3 randomPosition = Vector3.zero;
            int iterations = 0;
            while(obstacleMap.GetGlobalPointTravesibility(randomPosition) != ObstacleMap.Traversability.Free || iterations == 0)
            {
                randomPosition = new Vector3(UnityEngine.Random.Range(-46f, -26f) + _agent.PacManGameManager.transform.position.x, 0.4f, 
                    UnityEngine.Random.Range(-10f, 10f) + _agent.PacManGameManager.transform.position.z);
                iterations++;
                if (iterations > 100)
                {
                    Debug.LogWarning("Could not find a random position for respawn after 100 iterations.");
                    return transform.position;
                }
            }
            return randomPosition;
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;
            
            continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
            continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
        }
    }
}