using System;
using System.Collections.Generic;
using PacMan.Interface.PacMan;
using UnityEngine;
using Unity.MLAgents.Sensors;

namespace PacMan.RLAgent
{
    public class PacManRetreatAgent : PacManBaseGymAgent
    {
        private float _currentFoodCount;
        private float _currentFlowCost;
        private float _currentDistanceToHome;
        private bool _lostFoodSinceLastReward;
        private bool _hasLeftHomeTerritorySinceReset;

        protected override void OnInitialized()
        {
            ResetState();

            RewardWeights.Clear();
            RewardWeights["flowCost"] = 0.05f; // -1 in flow cost
            RewardWeights["eatFood"] = 0.2f; // Small bonus: retreat policy should not over-prioritize foraging
            RewardWeights["depositFood"] = 3f; // Strong reward for safely bringing food home
            RewardWeights["lostFood"] = -2f; // Penalize dropping food before reaching home
            RewardWeights["eatEnemy"] = 0f; // Not used for retreat policy
            RewardWeights["eatEnemyBonus"] = 0f; // Not used for retreat policy
            RewardWeights["eatenByEnemy"] = -3f; // Penalize being caught while retreating
            RewardWeights["living"] = -0.01f; // Each step
            RewardWeights["touchingWall"] = -0.01f; // Each step touching a wall
            RewardWeights["noMove"] = -0.03f; // Each step with low movement to encourage moving
            RewardWeights["retreatProgress"] = 0.5f; // Reward reducing distance to home border while carrying food
            RewardWeights["retreatBackslide"] = -0.03f; // Penalize moving away from home while carrying food
            RewardWeights["safeFood"] = 0.01f; // Small shaping reward for keeping food in home territory
        }

        protected void ResetState()
        {
            ResetCurrentAction();
            _currentFoodCount = 0;
            _currentFlowCost = 0;
            _currentDistanceToHome = PacManObservationCollector.GetDistanceToHomeBorder(Agent);
            RewardAccumulator = 0f;
            _lostFoodSinceLastReward = false;
            _hasLeftHomeTerritorySinceReset = false;
        }

        protected override void OnGymReset()
        {
            ResetState();
        }

        protected override void CollectAgentSpecificObservations(VectorSensor sensor)
        {
            AppendDefaultAgentSpecificObservations(sensor);
        }

        protected override void OnFixedStep()
        {
            // Require leaving home once before allowing "return home" episode termination.
            if (!_hasLeftHomeTerritorySinceReset && !PacManObservationCollector.IsInHomeTerritory(Agent))
            {
                _hasLeftHomeTerritorySinceReset = true;
            }
        }

        protected override float CollectReward()
        {
            // Food pickup
            var newFood = Agent.GetCarriedFoodCount() - _currentFoodCount;
            if (newFood > 0)
            {
                RewardAccumulator += newFood * RewardWeights["eatFood"];
            }
            else if (newFood < 0)
            {
                var changedFood = -newFood;
                if (!_lostFoodSinceLastReward && PacManObservationCollector.IsInHomeTerritory(Agent))
                    RewardAccumulator += changedFood * RewardWeights["depositFood"];
                else
                    RewardAccumulator += changedFood * RewardWeights["lostFood"];
            }
            
            _currentFoodCount = Agent.GetCarriedFoodCount();

            // flow cost dense reward
            var newFlowCost = Coordinator.GetFlowCost(Agent.transform.position);
            if(newFlowCost >= float.MaxValue)
                newFlowCost = _currentFlowCost;
            RewardAccumulator += (_currentFlowCost - newFlowCost) * RewardWeights["flowCost"];
            _currentFlowCost = newFlowCost;

            // Retreat shaping: reward getting closer to home when carrying food.
            var currentDistanceToHome = PacManObservationCollector.GetDistanceToHomeBorder(Agent);
            if (Agent.GetCarriedFoodCount() > 0f)
            {
                var homeProgress = _currentDistanceToHome - currentDistanceToHome;
                RewardAccumulator += homeProgress * RewardWeights["retreatProgress"];

                if (PacManObservationCollector.IsInHomeTerritory(Agent))
                {
                    RewardAccumulator += RewardWeights["safeFood"];
                }
                else if (homeProgress < 0f)
                {
                    RewardAccumulator += RewardWeights["retreatBackslide"];
                }
            }
            _currentDistanceToHome = currentDistanceToHome;
            
            //Living reward
            RewardAccumulator += RewardWeights["living"];
            
            // Stationary penalty
            if (CurrentAction.magnitude < 0.1f)
                RewardAccumulator += RewardWeights["noMove"];

            float returnReward = RewardAccumulator;
            RewardAccumulator = 0f;
            _lostFoodSinceLastReward = false;
            return returnReward; // So that we can add rewards during the whole episode
        }

        protected override bool ShouldEndEpisode(out bool interrupted)
        {
            interrupted = false;
            if (_hasLeftHomeTerritorySinceReset && PacManObservationCollector.IsInHomeTerritory(Agent))
            {
                return true;
            }

            return base.ShouldEndEpisode(out interrupted);
        }

        public virtual void OnCollisionStay(Collision other)
        {
            AddWallTouchPenalty(other);
        }
    }
}