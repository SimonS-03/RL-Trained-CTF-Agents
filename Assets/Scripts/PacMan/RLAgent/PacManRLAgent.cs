using System;
using System.Collections.Generic;
using System.Linq;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PacMan.RLAgent
{
    public class PacManRLAgent : GymAgent
    {
        [SerializeField]
        private int physicsStepsPerGymStep = 10;

        private PacManAgentManager _agent;
        private PacManMovementController _controller;
        private Vector2 _currentAction = Vector2.zero;
        private AgentCoordinator _coordinator;
        private const int enemyObservations = 4;
        private const int foodObservations = 6;
        private const int friendlyObservations = 3;

        // Observation layout (must stay aligned with Python Agent constants):
        // 0..17   : ego/map context (18)
        //          + time remaining, distance to home border
        // 18..49  : shared enemies      (4 * 8)
        // 50..73  : food observations   (6 * 4)
        // 74..97  : friendly agents     (3 * 8)
        private const int EgoObsDim = 18;
        private const int EnemyObsDim = 8;
        private const int FoodObsDim = 4;
        private const int FriendlyObsDim = 8;
        private const int EnemyStart = EgoObsDim;
        private const int FoodStart = EnemyStart + enemyObservations * EnemyObsDim;
        private const int FriendlyStart = FoodStart + foodObservations * FoodObsDim;
        private const int TotalObsDim = FriendlyStart + friendlyObservations * FriendlyObsDim;
        private float _currentFoodCount;
        private float _currentFlowCost;
        private float _currentTeamScoreDifference;
        private float _reward;
        private bool _lostFoodSinceLastReward;
        private Dictionary<string, float> _rewardWeights;
        private static readonly (int dx, int dz)[] rayDirections =
        {
            (-1, -1),
            (-1, 0),
            (-1, 1),
            (0, -1),
            (0, 1),
            (1, -1),
            (1, 0),
            (1, 1)
        };

        private void Awake()
        {
            GymVecEnvManager.Instance.physicsStepsPerGymStep = Mathf.Max(1, physicsStepsPerGymStep);
            _agent = GetComponent<PacManAgentManager>();
        }

        protected override void Initialize()
        {
            // Initialize coordinator as the gameObject under same parent that has the flag of my team
            _coordinator = transform.parent.GetComponentsInChildren<AgentCoordinator>().ToList().Find(coordinator => coordinator.isBlueTeam == (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue));
            _agent = GetComponent<PacManAgentManager>();
            //_coordinator = GetComponentsInParent<AgentCoordinator>().ToList().Find(coordinator => coordinator.isBlueTeam == (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue));
            if (_coordinator == null)
            {
                Debug.LogError("AgentCoordinator not found for PacManRLAgent team.");
            }
            else
            {
                _coordinator.RegisterAgent(this);
            }

            ResetState();

            _rewardWeights = new Dictionary<string, float>();
            _rewardWeights.Add("flowCost", 0.05f); // -1 in flow cost
            _rewardWeights.Add("eatFood", 1f); // +1 in carried food
            _rewardWeights.Add("depositFood", 2f); // * number of food deposited
            _rewardWeights.Add("lostFood", -1f); // * number of food lost
            _rewardWeights.Add("eatEnemy", 3f); // For 1 enemy
            _rewardWeights.Add("eatEnemyBonus", 0.5f); // for each food the enemy carried
            _rewardWeights.Add("eatenByEnemy", -3f); // for 1 enemy
            _rewardWeights.Add("living", -0.01f); // Each step
            _rewardWeights.Add("touchingWall", -0.01f); // Each step touching a wall
            _rewardWeights.Add("noMove", -0.03f); // Each step with low movement to encourage moving
        }

        protected void ResetState()
        {
            _currentAction = Vector2.zero;
            _currentFoodCount = 0;
            _currentFlowCost = 0;
            _reward = 0f;
            _lostFoodSinceLastReward = false;
            _currentTeamScoreDifference = _agent.GetScore();
        }

        protected override void CollectInfo(Dictionary<string, float> metadata)
        {
            metadata.Add("Red", _agent.CompareTag("Red") ? 1 : 0); // Just some proverbial food for thought
            metadata.Add("Blue", _agent.CompareTag("Blue") ? 1 : 0);
        }

        protected override void GymReset()
        {
            _coordinator.ResetEpisode();
            ResetState();
        }

        public void FixedUpdate()
        {
            _agent.Movement.ApplyDesiredControl(_currentAction);
            if (_coordinator != null)
            {
                _coordinator.ReportVisibleEnemies(_agent, _agent.GetVisibleEnemyAgents());
            }
        }

        protected override void SetAction(AgentAction agentAction)
        {
            _currentAction = new Vector2(agentAction.Continuous[0], agentAction.Continuous[1]);
        }

        protected override void CollectObservation(ref AgentObservation observation)
        {
            CollectObservation(observation, _agent, _coordinator);
        }

        public static AgentObservation CollectObservation(AgentObservation observation, PacManAgentManager thisAgent, AgentCoordinator coordinator)
        {
            // Fixed observation map :
            // ego[0:18], enemy[18:50], food[50:74], friendly[74:98]
            // total = 98

            // Observation ideas
            // - Nearby agents positions
            // - Whether another agent is closer to closest food

            observation.AppendContinuous((TeamAssignmentUtil.CheckTeam(thisAgent.gameObject) == Team.Blue) ? 1f : 0f);

            observation.AppendContinuous(thisAgent.IsGhost() ? 1f : 0f);
            observation.AppendContinuous(thisAgent.IsScared() ? 1f : 0f);
            observation.AppendContinuous(Mathf.Clamp01(thisAgent.GetScore() / 100f)); // Score difference ??
            // observation.AppendContinuous(thisAgent.GetTimeRemaining() / 300); // Normalization ?
            observation.AppendContinuous(Mathf.Clamp01(thisAgent.GetCarriedFoodCount() / 10f));

            observation.AppendContinuous(Mathf.Clamp(coordinator.GetFlowCost(thisAgent.transform.position)/30f, 0f, 1f));
            var flowDirection = coordinator.GetFlowDirection(thisAgent.transform.position);
            observation.AppendContinuous(Mathf.Clamp(flowDirection.x, -1f, 1f));
            observation.AppendContinuous(Mathf.Clamp(flowDirection.z, -1f, 1f));

            foreach(var (dx, dz) in rayDirections)
            {
                var direction = dx *  Vector3.right + dz * Vector3.forward;
                bool intersects = Physics.Raycast(thisAgent.transform.position, direction, out RaycastHit hitInfo, 10f, LayerMask.GetMask("Obstacle"));
                if(intersects)
                    observation.AppendContinuous(Mathf.Clamp01(hitInfo.distance / 10f));
                else
                    observation.AppendContinuous(1f);
            }

            observation.AppendContinuous(Mathf.Clamp01(thisAgent.GetTimeRemaining() / 300f));
            observation.AppendContinuous(Mathf.Clamp01(GetDistanceToHomeBorder(thisAgent) / 30f));

            var visibleEnemyAgents = coordinator != null
                ? coordinator.GetSharedVisibleEnemies()
                : thisAgent.GetVisibleEnemyAgents();
            int i = 0;
            while(i < Mathf.Min(visibleEnemyAgents.Count, enemyObservations))
            {
                PacManAgentManager enemy = visibleEnemyAgents[i];
                var relPos = enemy.transform.position - thisAgent.transform.position;
                var relVel = enemy.GetVelocity() - thisAgent.GetVelocity();
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                observation.AppendContinuous(enemy.IsGhost() ? 1f : 0f);
                observation.AppendContinuous(enemy.IsScared() ? 1f : 0f);
                observation.AppendContinuous(Mathf.Clamp01(enemy.GetScaredRemainingDuration() / 20f));
                observation.AppendContinuous(Mathf.Clamp01(enemy.GetCarriedFoodCount() / 10f));
                i++;
            }
            while(i < enemyObservations)
            {
                for(int j = 0; j < EnemyObsDim; j++)
                {
                    observation.AppendContinuous(0f);
                }
                i++;
            }

            var foodObjects = thisAgent.GetFoodObjects() ?? new List<GameObject>();
            var orderedFoodObjects = foodObjects
                .OrderBy(food => Vector3.Distance(food.transform.position, thisAgent.transform.position))
                .ToList();

            int foodIndex = 0;
            while (foodIndex < Mathf.Min(orderedFoodObjects.Count, foodObservations))
            {
                var food = orderedFoodObjects[foodIndex];
                var relPos = food.transform.position - thisAgent.transform.position;
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));

                // Distinguish currently on-ground food from eaten/carried food.
                observation.AppendContinuous(food.activeSelf ? 1f : 0f);
                observation.AppendContinuous(
                    TeamAssignmentUtil.CheckTeam(food) != TeamAssignmentUtil.CheckTeam(thisAgent.gameObject) ? 1f : 0f
                );
                foodIndex++;
            }
            while (foodIndex < foodObservations)
            {
                for (int j = 0; j < FoodObsDim; j++)
                {
                    observation.AppendContinuous(0f);
                }
                foodIndex++;
            }

            var friendlyAgents = thisAgent.GetFriendlyAgents() ?? new List<PacManAgentManager>();
            var orderedFriendlies = friendlyAgents
                .OrderBy(friend => Vector3.Distance(friend.transform.position, thisAgent.transform.position))
                .ToList();

            int friendlyIndex = 0;
            while (friendlyIndex < Mathf.Min(orderedFriendlies.Count, friendlyObservations))
            {
                var friendly = orderedFriendlies[friendlyIndex];
                var relPos = friendly.transform.position - thisAgent.transform.position;
                var relVel = friendly.GetVelocity() - thisAgent.GetVelocity();
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                observation.AppendContinuous(friendly.IsGhost() ? 1f : 0f);
                observation.AppendContinuous(friendly.IsScared() ? 1f : 0f);
                observation.AppendContinuous(Mathf.Clamp01(friendly.GetScaredRemainingDuration() / 20f));
                observation.AppendContinuous(Mathf.Clamp01(friendly.GetCarriedFoodCount() / 10f));
                friendlyIndex++;
            }
            while (friendlyIndex < friendlyObservations)
            {
                for (int j = 0; j < FriendlyObsDim; j++)
                {
                    observation.AppendContinuous(0f);
                }
                friendlyIndex++;
            }

            return observation;

            // Available :

            // thisAgent.GetTimeRemaining();
            // thisAgent.GetScore();
            // bool isGhost = thisAgent.IsGhost(); // Can eat pacman
            // bool isScared = thisAgent.IsScared(); // Can be eaten anywhere
            // float scaredDuration = thisAgent.GetScaredRemainingDuration();

            // float carriedFoodCount = thisAgent.GetCarriedFoodCount();

            // List<GameObject> foodPositions = thisAgent.GetFoodObjects(); // Positions of food or last know position of food
            // var activeFoodPositions = foodPositions.FindAll(food => food.activeSelf); // Food that is currently on the ground
            // var inactiveFoodLatestPositions = foodPositions.FindAll(food => !food.activeSelf); // Food that is currently carried. The gameObject position will report where it was picked up from. Might be useful in some scenarios.
            // List<GameObject> capsulePositions = thisAgent.GetCapsuleObjects();

            // // var isLocalPointTraversable = _obstacleMap?.GetLocalPointTraversibility(transform.localPosition);

            // var teamAgentManagers = thisAgent.GetTeamAgents(); //Agents in team, including this agent
            // var friendlyAgentManagers = thisAgent.GetFriendlyAgents(); //Agents in team, except this agent

            // // var visibleEnemyAgents = thisAgent.GetVisibleEnemyAgents(); // Enemy agents in LoS. Know percise information
            // PacManObservations fetchEnemyObservations = thisAgent.GetEnemyObservations(); // Enemies out of LoS. Know partial information. 
            // if (fetchEnemyObservations.Observations.Length > 0)
            // {
            //     // Debug.Log(fetchEnemyObservations.ObservationFixedTime);
            // }

            // // Since the RigidBody is updated server side and the client only syncs position, rigidbody.Velocity does not report a velocity
            // var velocity = thisAgent.GetVelocity(); // Use the manager method to get the true velocity from the server
            // friendlyAgentManager.GetVelocity(); // Given the damping, max velocity magnitude is around 2.34
        }

        protected override float CollectReward()
        {
            // Food pickup
            var newFood = _agent.GetCarriedFoodCount() - _currentFoodCount;
            if (newFood > 0)
                _reward += newFood * _rewardWeights["eatFood"];
            else if (newFood < 0)
            {
                var changedFood = -newFood;
                if (!_lostFoodSinceLastReward && IsInHomeTerritory(_agent))
                    _reward += changedFood * _rewardWeights["depositFood"];
                else
                    _reward += changedFood * _rewardWeights["lostFood"];
            }

            // else reward if dropped in teritory or penalize if dead
            _currentFoodCount = _agent.GetCarriedFoodCount();

            // flow cost dense reward
            // var newFlowCost = _coordinator.GetFlowCost(_agent.transform.position);
            // if(newFlowCost >= float.MaxValue)
            //     newFlowCost = _currentFlowCost;
            // if(newFlowCost < _currentFlowCost) // Closer to food
            //     _reward += (_currentFlowCost - newFlowCost) * _rewardWeights["flowCost"];
            // _currentFlowCost = newFlowCost;
            
            //Living reward
            _reward += _rewardWeights["living"];

            // Team score difference reward (for now just score)
            var teamScoreDifference = _agent.GetScore() - _currentTeamScoreDifference;
            _reward += teamScoreDifference * 0.005f; // Scale down the score difference reward
            _currentTeamScoreDifference = _agent.GetScore();
            
            // Stationary penalty
            if (_currentAction.magnitude < 0.1f)
                _reward += _rewardWeights["noMove"]; // Reuse wall penalty for standing still to encourage

            float returnReward = _reward;
            _reward = 0f;
            _lostFoodSinceLastReward = false;
            return returnReward; // So that we can add rewards during the whole episode
        }

        protected override EnvironmentState GymStep()
        {
            if(_coordinator.timedOut)
                return EnvironmentState.Truncated;
            if(_coordinator.done)
                return EnvironmentState.Done;
            return EnvironmentState.Running;
        }

        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;
            if (isEnemyAgent && !otherAgent.isScared && (!_agent.IsGhost() && otherAgent.IsGhost() ||
                                                        _agent.IsGhost() && otherAgent.IsGhost() ||
                                                        !_agent.IsGhost() && !otherAgent.IsGhost()))
            {
                // Eaten by other agent:
                // - In their territory
                // - Ghost collision at border
                // - Non-ghost collision at border
                // drops food, killed, back to start
                _reward += _rewardWeights["eatenByEnemy"];
                _lostFoodSinceLastReward = true;
            }

            if (isEnemyAgent && _agent.IsGhost() && _agent.isScared && !otherAgent.IsGhost())
            {
                // Eaten by powered up enemy in my territory
                _reward += _rewardWeights["eatenByEnemy"];
                _lostFoodSinceLastReward = true;
            }

            if (isEnemyAgent && !_agent.isScared && _agent.IsGhost() && !otherAgent.IsGhost())
            {
                // I eat an agent in my territory
                _reward += _rewardWeights["eatEnemy"] * (1f + _rewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount()); // Bonus for eating an enemy carrying more food
            }

            if(isEnemyAgent && otherAgent.IsGhost() && otherAgent.isScared && !_agent.IsGhost())
            {
                // I eat an agent in their territory
                _reward += _rewardWeights["eatEnemy"] * (1f + _rewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
            }
        }

        private static bool IsInHomeTerritory(PacManAgentManager agent)
        {
            if (agent == null)
            {
                return false;
            }

            var x = agent.transform.localPosition.x;
            if (TeamAssignmentUtil.CheckTeam(agent.gameObject) == Team.Blue)
            {
                return x >= -0.3f;
            }
            return x <= 0.3f;
        }

        private static float GetDistanceToHomeBorder(PacManAgentManager agent)
        {
            if (agent == null)
            {
                return 0f;
            }

            var homeBorderX = TeamAssignmentUtil.CheckTeam(agent.gameObject) == Team.Blue ? -0.3f : 0.3f;
            return Mathf.Abs(agent.transform.localPosition.x - homeBorderX);
        }

        public virtual void OnCollisionStay(Collision other)
        {
            // Touching a wall
            if(other.gameObject.CompareTag("Obstacle"))
            {
                _reward += _rewardWeights["touchingWall"];
            }
        }

        protected override AgentAction ProduceDummyAction(AgentAction dummyAgentAction)
        {
            var x = 0;
            var z = 0;
            if (Input.GetKey("w"))
            {
                z = 1;
            }

            if (Input.GetKey("a"))
            {
                x = -1;
            }

            if (Input.GetKey("s"))
            {
                z = -1;
            }

            if (Input.GetKey("d"))
            {
                x = 1;
            }

            dummyAgentAction.Continuous[0] = x;
            dummyAgentAction.Continuous[1] = z;
            return dummyAgentAction;
        }
    }
}