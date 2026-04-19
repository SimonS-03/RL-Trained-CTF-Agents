using System;
using System.Collections.Generic;
using System.Linq;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using MLAgent = Unity.MLAgents.Agent;

namespace PacMan.RLAgent
{
    public class PacManRLAgent : MLAgent
    {
        [SerializeField]
        private int decisionInterval = 10;
        private PacManAgentManager _agent;
        private Vector2 _currentAction = Vector2.zero;
        private AgentCoordinator _coordinator;
        private const int enemyObservations = 4;
        private const int foodObservations = 6;
        private const int friendlyObservations = 3;

        // Observation layout (must stay aligned with Python Agent constants):
        // 0..17   : ego/map context (18)
        // 18..49  : shared enemies      (4 * 8)
        // 50..73  : food observations   (6 * 4)
        // 74..97  : friendly agents     (3 * 8)
        private const int EnemyObsDim = 8;
        private const int FoodObsDim = 4;
        private const int FriendlyObsDim = 8;

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
            _agent = GetComponent<PacManAgentManager>();
        }

        public override void Initialize()
        {
            var isBlueTeam = TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue;
            _coordinator = transform.parent
                .GetComponentsInChildren<AgentCoordinator>()
                .ToList()
                .Find(coordinator => coordinator.isBlueTeam == isBlueTeam);

            _agent = GetComponent<PacManAgentManager>();
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
            _rewardWeights.Add("flowCost", 0.05f);
            _rewardWeights.Add("eatFood", 1f);
            _rewardWeights.Add("depositFood", 2f);
            _rewardWeights.Add("lostFood", -1f);
            _rewardWeights.Add("eatEnemy", 3f);
            _rewardWeights.Add("eatEnemyBonus", 0.5f);
            _rewardWeights.Add("eatenByEnemy", -3f);
            _rewardWeights.Add("living", -0.01f);
            _rewardWeights.Add("touchingWall", -0.01f);
            _rewardWeights.Add("noMove", -0.03f);
        }

        public override void OnEpisodeBegin()
        {
            _coordinator?.ResetEpisode();
            ResetState();
        }

        public void FixedUpdate()
        {
            _agent.Movement.ApplyDesiredControl(_currentAction);
            if (_coordinator != null)
            {
                _coordinator.ReportVisibleEnemies(_agent, _agent.GetVisibleEnemyAgents());
            }

            if (_coordinator == null)
            {
                return;
            }

            if (_coordinator.timedOut)
            {
                EpisodeInterrupted();
            }
            else if (_coordinator.done)
            {
                EndEpisode();
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            _currentAction = new Vector2(actions.ContinuousActions[0], actions.ContinuousActions[1]);
            AddReward(CollectReward());
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            CollectObservations(sensor.AddObservation, _agent, _coordinator);
        }

        public static List<float> CollectObservationList(PacManAgentManager thisAgent, AgentCoordinator coordinator)
        {
            var values = new List<float>(98);
            CollectObservations(values.Add, thisAgent, coordinator);
            return values;
        }

        private static void CollectObservations(Action<float> append, PacManAgentManager thisAgent, AgentCoordinator coordinator)
        {
            append((TeamAssignmentUtil.CheckTeam(thisAgent.gameObject) == Team.Blue) ? 1f : 0f);

            append(thisAgent.IsGhost() ? 1f : 0f);
            append(thisAgent.IsScared() ? 1f : 0f);
            append(Mathf.Clamp01(thisAgent.GetScore() / 100f));
            append(Mathf.Clamp01(thisAgent.GetCarriedFoodCount() / 10f));

            if (coordinator != null)
            {
                append(Mathf.Clamp(coordinator.GetFlowCost(thisAgent.transform.position) / 30f, 0f, 1f));
                var flowDirection = coordinator.GetFlowDirection(thisAgent.transform.position);
                append(Mathf.Clamp(flowDirection.x, -1f, 1f));
                append(Mathf.Clamp(flowDirection.z, -1f, 1f));
            }
            else
            {
                append(1f);
                append(0f);
                append(0f);
            }

            foreach (var (dx, dz) in rayDirections)
            {
                var direction = dx * Vector3.right + dz * Vector3.forward;
                bool intersects = Physics.Raycast(thisAgent.transform.position, direction, out RaycastHit hitInfo, 10f, LayerMask.GetMask("Obstacle"));
                if (intersects)
                {
                    append(Mathf.Clamp01(hitInfo.distance / 10f));
                }
                else
                {
                    append(1f);
                }
            }

            append(Mathf.Clamp01(thisAgent.GetTimeRemaining() / 300f));
            append(Mathf.Clamp01(GetDistanceToHomeBorder(thisAgent) / 30f));

            var visibleEnemyAgents = coordinator != null
                ? coordinator.GetSharedVisibleEnemies()
                : thisAgent.GetVisibleEnemyAgents();
            int i = 0;
            while (i < Mathf.Min(visibleEnemyAgents.Count, enemyObservations))
            {
                PacManAgentManager enemy = visibleEnemyAgents[i];
                var relPos = enemy.transform.position - thisAgent.transform.position;
                var relVel = enemy.GetVelocity() - thisAgent.GetVelocity();
                append(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                append(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                append(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                append(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                append(enemy.IsGhost() ? 1f : 0f);
                append(enemy.IsScared() ? 1f : 0f);
                append(Mathf.Clamp01(enemy.GetScaredRemainingDuration() / 20f));
                append(Mathf.Clamp01(enemy.GetCarriedFoodCount() / 10f));
                i++;
            }
            while (i < enemyObservations)
            {
                for (int j = 0; j < EnemyObsDim; j++)
                {
                    append(0f);
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
                append(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                append(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                append(food.activeSelf ? 1f : 0f);
                append(TeamAssignmentUtil.CheckTeam(food) != TeamAssignmentUtil.CheckTeam(thisAgent.gameObject) ? 1f : 0f);
                foodIndex++;
            }
            while (foodIndex < foodObservations)
            {
                for (int j = 0; j < FoodObsDim; j++)
                {
                    append(0f);
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
                append(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                append(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                append(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                append(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                append(friendly.IsGhost() ? 1f : 0f);
                append(friendly.IsScared() ? 1f : 0f);
                append(Mathf.Clamp01(friendly.GetScaredRemainingDuration() / 20f));
                append(Mathf.Clamp01(friendly.GetCarriedFoodCount() / 10f));
                friendlyIndex++;
            }
            while (friendlyIndex < friendlyObservations)
            {
                for (int j = 0; j < FriendlyObsDim; j++)
                {
                    append(0f);
                }
                friendlyIndex++;
            }
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActionsOut = actionsOut.ContinuousActions;
            var x = 0f;
            var z = 0f;
            if (Input.GetKey("w"))
            {
                z = 1f;
            }

            if (Input.GetKey("a"))
            {
                x = -1f;
            }

            if (Input.GetKey("s"))
            {
                z = -1f;
            }

            if (Input.GetKey("d"))
            {
                x = 1f;
            }

            continuousActionsOut[0] = x;
            continuousActionsOut[1] = z;
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

        protected float CollectReward()
        {
            var newFood = _agent.GetCarriedFoodCount() - _currentFoodCount;
            if (newFood > 0)
            {
                _reward += newFood * _rewardWeights["eatFood"];
            }
            else if (newFood < 0)
            {
                var changedFood = -newFood;
                if (!_lostFoodSinceLastReward && IsInHomeTerritory(_agent))
                {
                    _reward += changedFood * _rewardWeights["depositFood"];
                }
                else
                {
                    _reward += changedFood * _rewardWeights["lostFood"];
                }
            }

            _currentFoodCount = _agent.GetCarriedFoodCount();

            var newFlowCost = _coordinator != null ? _coordinator.GetFlowCost(_agent.transform.position) : _currentFlowCost;
            if (newFlowCost >= float.MaxValue)
            {
                newFlowCost = _currentFlowCost;
            }
            _reward += (_currentFlowCost - newFlowCost) * _rewardWeights["flowCost"];
            _currentFlowCost = newFlowCost;

            _reward += _rewardWeights["living"];

            var teamScoreDifference = _agent.GetScore() - _currentTeamScoreDifference;
            _reward += teamScoreDifference * 0.005f;
            _currentTeamScoreDifference = _agent.GetScore();

            if (_currentAction.magnitude < 0.1f)
            {
                _reward += _rewardWeights["noMove"];
            }

            float returnReward = _reward;
            _reward = 0f;
            _lostFoodSinceLastReward = false;
            return returnReward;
        }

        public virtual void OnCollisionEnter(Collision other)
        {
            var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
            bool isEnemyAgent = otherAgent != null && other.gameObject.tag != tag;
            if (isEnemyAgent && !otherAgent.isScared && (!_agent.IsGhost() && otherAgent.IsGhost() ||
                                                        _agent.IsGhost() && otherAgent.IsGhost() ||
                                                        !_agent.IsGhost() && !otherAgent.IsGhost()))
            {
                _reward += _rewardWeights["eatenByEnemy"];
                _lostFoodSinceLastReward = true;
            }

            if (isEnemyAgent && _agent.IsGhost() && _agent.isScared && !otherAgent.IsGhost())
            {
                _reward += _rewardWeights["eatenByEnemy"];
                _lostFoodSinceLastReward = true;
            }

            if (isEnemyAgent && !_agent.isScared && _agent.IsGhost() && !otherAgent.IsGhost())
            {
                _reward += _rewardWeights["eatEnemy"] * (1f + _rewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
            }

            if (isEnemyAgent && otherAgent.IsGhost() && otherAgent.isScared && !_agent.IsGhost())
            {
                _reward += _rewardWeights["eatEnemy"] * (1f + _rewardWeights["eatEnemyBonus"] * otherAgent.GetCarriedFoodCount());
            }
        }

        public virtual void OnCollisionStay(Collision other)
        {
            if (other.gameObject.CompareTag("Obstacle"))
            {
                _reward += _rewardWeights["touchingWall"];
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

    }
}
