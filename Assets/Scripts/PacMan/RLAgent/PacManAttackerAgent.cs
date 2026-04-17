using PacMan.Interface.PacMan;
using Scripts.VecEnv.Message;
using UnityEngine;
using PacMan.Local;

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

        protected override void CollectAgentSpecificObservations(ref AgentObservation observation)
        {
            //AppendDefaultAgentSpecificObservations(ref observation);
                        
            // Append role flag: 1.0 for attacker (enables routing to attacker networks)
            observation.AppendContinuous(1.0f);
        }

        protected override void CollectObservation(ref AgentObservation observation)
        {
            // Collect shared observations and role-specific observations
            base.CollectObservation(ref observation);
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

        protected override EnvironmentState GymStep()
        {
            return base.GymStep();
        }

        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;

            if(isEnemyAgent)
            {
                RewardAccumulator += RewardWeights["eatEnemy"] * (1f + RewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
                mustRespawnRandomly = true; // GameManager will respawn at start location so we have to override this
            }
        }

        public virtual void OnCollisionStay(Collision other)
        {
            AddWallTouchPenalty(other);
        }
    }
}
