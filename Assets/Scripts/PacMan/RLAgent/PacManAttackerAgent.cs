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

        protected override void OnInitialized()
        {
            ResetState();

            RewardWeights.Clear();
            RewardWeights["eatEnemy"] = 8f; // Primary objective: eliminate vulnerable enemies
            RewardWeights["eatEnemyBonus"] = 0.3f; // Bonus for eating enemies carrying food
            RewardWeights["eatenByEnemy"] = -5f; // Severe penalty for being caught
            RewardWeights["living"] = -0.01f; // Step cost
            RewardWeights["touchingWall"] = -0.01f; // Discourage collisions
            RewardWeights["noMove"] = -0.03f; // Encourage movement
            RewardWeights["enemiesInLoS"] = 0.3f; // Reward for having more enemies in line of sight
        }

        protected void ResetState()
        {
            ResetCurrentAction();
            enemiesInLoS = 0;
            RewardAccumulator = 0f;
            mustRespawnRandomly = false;
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
            var newEnemiesInLoS = Agent.GetVisibleEnemyAgents().Count;
            RewardAccumulator += RewardWeights["enemiesInLoS"] * (newEnemiesInLoS - enemiesInLoS);
            enemiesInLoS = newEnemiesInLoS;

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
