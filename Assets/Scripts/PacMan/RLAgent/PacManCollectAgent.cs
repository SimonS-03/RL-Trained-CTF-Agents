using System;
using System.Collections.Generic;
using PacMan.Interface.PacMan;
using Scripts.VecEnv.Message;
using UnityEngine;
using PacMan.Local;


namespace PacMan.RLAgent
{
    public class PacManCollectAgent : PacManBaseGymAgent
    {
        private float _currentFoodCount;
        private float _currentFlowCost;

        protected override void OnInitialized()
        {
            ResetState();

            RewardWeights.Clear();
            RewardWeights["flowCost"] = 0.05f; // -1 in flow cost
            RewardWeights["eatFood"] = 2.5f; // Strong reward for collecting food
            RewardWeights["depositFood"] = 0f; // Not the main goal for Collect agent
            RewardWeights["lostFood"] = -0.5f; // Minimal penalty; focus is on collecting, not returning
            RewardWeights["eatEnemy"] = 2f; // Useful while foraging to clear threats
            RewardWeights["eatEnemyBonus"] = 0.2f; // Bonus when enemy carries food
            RewardWeights["eatenByEnemy"] = -2f; // Avoid being caught while collecting
            RewardWeights["living"] = -0.01f; // Each step
            RewardWeights["touchingWall"] = -0.01f; // Each step touching a wall
            RewardWeights["noMove"] = -0.03f; // Each step with low movement
            RewardWeights["collectProgress"] = 0.3f; // Reward for moving toward food
        }

        protected void ResetState()
        {
            ResetCurrentAction();
            _currentFoodCount = 0;
            _currentFlowCost = 0;
            RewardAccumulator = 0f;
        }

        protected override void OnGymReset()
        {
            ResetState();
        }

        protected override void CollectAgentSpecificObservations(ref AgentObservation observation)
        {
            AppendDefaultAgentSpecificObservations(ref observation);
        }

        protected override float CollectReward()
        {
            // Food pickup — primary objective
            var newFood = Agent.GetCarriedFoodCount() - _currentFoodCount;
            if (newFood > 0)
            {
                RewardAccumulator += newFood * RewardWeights["eatFood"];
            }
            else if (newFood < 0)
            {
                RewardAccumulator += (-newFood) * RewardWeights["lostFood"];
            }
            
            _currentFoodCount = Agent.GetCarriedFoodCount();

            // flow cost dense reward
            var newFlowCost = Coordinator.GetFlowCost(Agent.transform.position);
            if(newFlowCost >= float.MaxValue)
                newFlowCost = _currentFlowCost;
            RewardAccumulator += (_currentFlowCost - newFlowCost) * RewardWeights["flowCost"];
            _currentFlowCost = newFlowCost;
            
            //Living reward
            RewardAccumulator += RewardWeights["living"];
            
            // Stationary penalty
            if (CurrentAction.magnitude < 0.1f)
                RewardAccumulator += RewardWeights["noMove"];

            float returnReward = RewardAccumulator;
            RewardAccumulator = 0f;
            return returnReward; // So that we can add rewards during the whole episode
        }


        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;
            
            // Eating enemies while foraging is beneficial for a collecting agent
            if (isEnemyAgent && !Agent.isScared && Agent.IsGhost() && !otherAgent.IsGhost())
            {
                RewardAccumulator += RewardWeights["eatEnemy"] * (1f + RewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
            }

            if(isEnemyAgent && otherAgent.IsGhost() && otherAgent.isScared && !Agent.IsGhost())
            {
                RewardAccumulator += RewardWeights["eatEnemy"] * (1f + RewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
            }

            // Being caught while collecting is costly
            if (isEnemyAgent && !otherAgent.isScared && (!Agent.IsGhost() && otherAgent.IsGhost() ||
                                                        Agent.IsGhost() && otherAgent.IsGhost() ||
                                                        !Agent.IsGhost() && !otherAgent.IsGhost()))
            {
                RewardAccumulator += RewardWeights["eatenByEnemy"];
            }

            if (isEnemyAgent && Agent.IsGhost() && Agent.isScared && !otherAgent.IsGhost())
            {
                RewardAccumulator += RewardWeights["eatenByEnemy"];
            }
        }

        public virtual void OnCollisionStay(Collision other)
        {
            AddWallTouchPenalty(other);
        }
    }
}