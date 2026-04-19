using System;
using System.Collections.Generic;
using PacMan.Game;
using PacMan.Interface.PacMan;
using UnityEngine;
using PacMan.Local;
using Unity.MLAgents.Sensors;

namespace PacMan.RLAgent
{
    /// <summary>
    /// Attacker agent: focuses on invading enemy territory and eliminating threats.
    /// Primary objectives:
    /// - Eat enemy agents (ghosts in enemy territory)
    /// - Avoid getting caught by defenders
    /// - Maintain map control through presence
    /// </summary>
    public class PacManAttackerAgent : PacManBaseGymAgent
    {
        private int enemiesInLoS;
        private bool mustRespawnRandomly;
        private float previousDistanceToClosestEnemy;
        protected override void OnInitialized()
        {
            ResetState();

            RewardWeights.Clear();
            RewardWeights["eatEnemy"] = 8f; // Primary objective: eliminate vulnerable enemies
            RewardWeights["eatEnemyBonus"] = 0.3f; // Bonus for eating enemies carrying food
            RewardWeights["eatenByEnemy"] = -5f; // Severe penalty for being caught
            RewardWeights["living"] = -0.01f; // Step cost
            RewardWeights["touchingWall"] = -0.01f; // Discourage collisions
            RewardWeights["noMove"] = -0.02f; // Encourage movement
            RewardWeights["enemiesInLoS"] = 0.3f; // Reward for having more enemies in line of sight
            RewardWeights["proximityToEnemy"] = 0.2f; // Reward for being closer to visible enemies
        }

        protected void ResetState()
        {
            ResetCurrentAction();
            enemiesInLoS = 0;
            RewardAccumulator = 0f;
            mustRespawnRandomly = false;
            previousDistanceToClosestEnemy = float.MaxValue;
        }

        protected override void OnGymReset()
        {
            ResetState();
        }

        protected override void OnFixedStep()
        {
            if (mustRespawnRandomly)
            {
                Coordinator.RespawnAgentRandomly(this);
                mustRespawnRandomly = false;
            }
        }

        protected override void CollectAgentSpecificObservations(VectorSensor sensor)
        {
            //AppendDefaultAgentSpecificObservations(ref observation);
                        
            // Append role flag: 1.0 for attacker (enables routing to attacker networks)
            // sensor.AddObservation(1.0f);
        }

        public static List<float> CollectObservationList(PacManAgentManager thisAgent, AgentCoordinator coordinator)
        {
            var values = new List<float>(104);
            CollectAgentSpecificObservationList(values.Add, thisAgent, coordinator);
            values.AddRange(PacManObservationCollector.CollectObservationList(thisAgent, coordinator));
            return values;
        }

        private static void CollectAgentSpecificObservationList(
            Action<float> append,
            PacManAgentManager thisAgent,
            AgentCoordinator coordinator)
        {
            // Keep empty for now, but route inference through attacker class so this can diverge later.
        }

        protected override float CollectReward()
        {
            // Living reward
            RewardAccumulator += RewardWeights["living"];

            // Movement encouragement
            if (CurrentAction.magnitude < 0.1f)
            {
                RewardAccumulator += RewardWeights["noMove"];
            }

            // Reward having more enemies in LoS
            var visibleEnemies = Agent.GetVisibleEnemyAgents();
            var newEnemiesInLoS = visibleEnemies.Count;
            RewardAccumulator += RewardWeights["enemiesInLoS"] * (newEnemiesInLoS - enemiesInLoS);
            enemiesInLoS = newEnemiesInLoS;

            var distanceToClosestEnemy = float.MaxValue;
            foreach (var enemy in visibleEnemies)
            {
                var dist = Vector3.Distance(Agent.transform.position, enemy.transform.position);
                if (dist < distanceToClosestEnemy)
                {
                    distanceToClosestEnemy = dist;
                }
            }
            if(previousDistanceToClosestEnemy != float.MaxValue)
            {
                RewardAccumulator += RewardWeights["proximityToEnemy"] * (Mathf.Clamp01(previousDistanceToClosestEnemy / 30f) - Mathf.Clamp01(distanceToClosestEnemy / 30f));
            }
            previousDistanceToClosestEnemy = distanceToClosestEnemy;

            float returnReward = RewardAccumulator;
            RewardAccumulator = 0f;
            return returnReward;
        }

        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;

            if (!isEnemyAgent)
            {
                return;
            }
            
            RewardAccumulator += RewardWeights["eatEnemy"];
            
        }

        public virtual void OnCollisionStay(Collision other)
        {
            AddWallTouchPenalty(other);
        }
    }
}
