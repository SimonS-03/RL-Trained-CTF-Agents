using System.Collections.Generic;
using PacMan.Interface.PacMan;
using Scripts.VecEnv.Message;
using UnityEngine;
using PacMan.Local;


namespace PacMan.RLAgent
{
    /// <summary>
    /// Defender agent: focuses on protecting home territory and food.
    /// Primary objectives:
    /// - Intercept and stop enemy agents before they secure food
    /// - Protect carried food and prevent theft
    /// - Control home territory through strategic positioning
    /// </summary>
    public class PacManDefenderAgent : PacManBaseGymAgent
    {
        private int enemiesInLoS;
        private bool mustRespawnRandomly;

        protected override void OnInitialized()
        {
            ResetState();

            RewardWeights.Clear();
            RewardWeights["eatEnemy"] = 3f; // Reward for catching enemies in home territory
            RewardWeights["eatEnemyBonus"] = 0.5f; // Extra reward for catching enemies carrying food
            RewardWeights["eatenByEnemy"] = -8f; // Penalty for being caught
            RewardWeights["living"] = -0.01f; // Step cost
            RewardWeights["touchingWall"] = -0.01f; // Discourage obstacles
            RewardWeights["noMove"] = -0.03f; // Encourage movement
            RewardWeights["enemiesInLoS"] = 0.3f; // Reward for having more enemies in line of sight
        }

        protected void ResetState()
        {
            ResetCurrentAction();
            RewardAccumulator = 0f;
            enemiesInLoS = 0;
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
            // AppendDefaultAgentSpecificObservations(ref observation);
            // Doesn't need food or home, just needs to flee

            // Append role flag: 0.0 for defender (routes to defender networks)
            observation.AppendContinuous(0f);
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

            // Reward losing LoS to enemies
            var newEnemiesInLoS = Agent.GetVisibleEnemyAgents().Count;
            RewardAccumulator += RewardWeights["enemiesInLoS"] * (enemiesInLoS - newEnemiesInLoS);
            enemiesInLoS = newEnemiesInLoS;

            float returnReward = RewardAccumulator;
            RewardAccumulator = 0f;
            return returnReward;
        }

        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;

            if(isEnemyAgent)
            {
                RewardAccumulator += RewardWeights["eatenByEnemy"] * (1f + RewardWeights["eatEnemyBonus"] * Agent.GetCarriedFoodCount());
                mustRespawnRandomly = true; // GameManager will respawn at start location so we have to override this
            }
        }

        public virtual void OnCollisionStay(Collision other)
        {
            AddWallTouchPenalty(other);
        }
    }
}
