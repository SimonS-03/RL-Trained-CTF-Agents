using System.Collections.Generic;
using System.Linq;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using MLAgent = Unity.MLAgents.Agent;

namespace PacMan.RLAgent
{
    public abstract class PacManBaseGymAgent : MLAgent
    {
        [SerializeField]
        private int decisionInterval = 10;

        protected PacManAgentManager Agent { get; private set; }
        protected AgentCoordinator Coordinator { get; private set; }
        protected Vector2 CurrentAction { get; private set; } = Vector2.zero;
        protected float RewardAccumulator { get; set; }
        protected Dictionary<string, float> RewardWeights { get; } = new();

        protected override void Awake()
        {
            base.Awake();
            Agent = GetComponent<PacManAgentManager>();
        }

        public override void Initialize()
        {
            Agent ??= GetComponent<PacManAgentManager>();
            Coordinator = ResolveCoordinator();
            if (Coordinator == null)
            {
                Debug.LogError("AgentCoordinator not found for PacMan agent team.");
            }
            else
            {
                Coordinator.RegisterAgent(this);
            }

            OnInitialized();
        }

        protected virtual void OnInitialized()
        {
        }

        public override void OnEpisodeBegin()
        {
            Coordinator?.ResetEpisode();
            OnGymReset();
        }

        protected virtual void OnGymReset()
        {
        }

        public virtual void FixedUpdate()
        {
            Agent.Movement.ApplyDesiredControl(CurrentAction);
            OnFixedStep();

            if (Coordinator != null)
            {
                Coordinator.ReportVisibleEnemies(Agent, Agent.GetVisibleEnemyAgents());
            }

            if (ShouldEndEpisode(out var interrupted))
            {
                if (interrupted)
                {
                    EpisodeInterrupted();
                }
                else
                {
                    EndEpisode();
                }
            }
        }

        protected virtual void OnFixedStep()
        {
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            CurrentAction = new Vector2(actions.ContinuousActions[0], actions.ContinuousActions[1]);
            AddReward(CollectReward());
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            CollectAgentSpecificObservations(sensor);
            PacManObservationCollector.CollectObservations(sensor, Agent, Coordinator);
        }

        protected virtual void CollectAgentSpecificObservations(VectorSensor sensor)
        {
        }

        protected virtual float CollectReward()
        {
            return 0f;
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

        protected void AppendDefaultAgentSpecificObservations(VectorSensor sensor)
        {
            // Self state and local status.
            sensor.AddObservation((TeamAssignmentUtil.CheckTeam(Agent.gameObject) == Team.Blue) ? 1f : 0f);
            sensor.AddObservation(Agent.IsGhost() ? 1f : 0f);
            sensor.AddObservation(Agent.IsScared() ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(Agent.GetScore() / 100f));
            sensor.AddObservation(Mathf.Clamp01(Agent.GetCarriedFoodCount() / 10f));

            AppendFoodEntityObservations(sensor);
            AppendHomeObservations(sensor);
        }

        protected void AppendFoodEntityObservations(VectorSensor sensor)
        {
            // Flow cost and dir
            // Food objects
            // Nearest food distance and density
            if(Coordinator != null)
            {
                var flowDirection = Coordinator.GetFlowDirection(Agent.transform.position);
                sensor.AddObservation(Mathf.Clamp(flowDirection.x, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(flowDirection.z, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(Coordinator.GetFlowCost(Agent.transform.position) / 30f, 0f, 1f));
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    sensor.AddObservation(0f);
                }
            }

            const int foodObservations = 6;
            const int foodObsDim = 4;

            var foodObjects = Agent.GetFoodObjects() ?? new List<GameObject>();
            var orderedFoodObjects = foodObjects
                .OrderBy(food => Vector3.Distance(food.transform.position, Agent.transform.position))
                .ToList();

            int foodIndex = 0;
            int foodDensity = 0;
            float closestFoodDist = float.MaxValue;
            while (foodIndex < Mathf.Min(orderedFoodObjects.Count, foodObservations))
            {
                var food = orderedFoodObjects[foodIndex];
                var relPos = food.transform.position - Agent.transform.position;
                sensor.AddObservation(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                sensor.AddObservation(food.activeSelf ? 1f : 0f);
                sensor.AddObservation(
                    TeamAssignmentUtil.CheckTeam(food) != TeamAssignmentUtil.CheckTeam(Agent.gameObject) ? 1f : 0f
                );
                
                if(food.activeSelf)
                {    
                    var distToFood = Vector3.Distance(food.transform.position, Agent.transform.position);
                    if (distToFood <= 15f)
                        foodDensity++;

                    if (distToFood < closestFoodDist)
                        closestFoodDist = distToFood;
                }

                foodIndex++;
            }

            while (foodIndex < foodObservations)
            {
                for (int j = 0; j < foodObsDim; j++)
                {
                    sensor.AddObservation(0f);
                }
                foodIndex++;
            }

            sensor.AddObservation(Mathf.Clamp01(foodDensity / 10f));
            sensor.AddObservation(closestFoodDist == float.MaxValue ? 1f : Mathf.Clamp01(closestFoodDist / 30f));
        }

        protected void AppendHomeObservations(VectorSensor sensor)
        {
            // Navigation and return-home guidance.
            if (Coordinator != null)
            {
                var homeFlowDirection = Coordinator.GetFlowDirectionToHome(Agent.transform.position);
                sensor.AddObservation(Mathf.Clamp(homeFlowDirection.x, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(homeFlowDirection.z, -1f, 1f));
                float distanceToHomeSafe = Coordinator.GetFlowCostToHome(Agent.transform.position);
                if (distanceToHomeSafe >= float.MaxValue)
                {
                    distanceToHomeSafe = PacManObservationCollector.GetDistanceToHomeBorder(Agent);
                }
                sensor.AddObservation(Mathf.Clamp01(distanceToHomeSafe / 50f));
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    sensor.AddObservation(0f);
                }
            }
        }

        protected virtual bool ShouldEndEpisode(out bool interrupted)
        {
            interrupted = false;
            if (Coordinator == null)
            {
                return false;
            }

            if (Coordinator.timedOut)
            {
                interrupted = true;
                return true;
            }

            if (Coordinator.done)
            {
                return true;
            }

            return false;
        }

        protected void ResetCurrentAction()
        {
            CurrentAction = Vector2.zero;
        }

        protected void AddWallTouchPenalty(Collision other)
        {
            if (other.gameObject.CompareTag("Obstacle") && RewardWeights.TryGetValue("touchingWall", out var wallPenalty))
            {
                RewardAccumulator += wallPenalty;
            }
        }

        private AgentCoordinator ResolveCoordinator()
        {
            var parent = transform.parent;
            var isBlueTeam = TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue;
            return parent.GetComponentsInChildren<AgentCoordinator>()
                .ToList()
                .Find(coordinator => coordinator.isBlueTeam == isBlueTeam);
        }

    }
}
