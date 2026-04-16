using System.Collections.Generic;
using System.Linq;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;
using UnityEngine;

namespace PacMan.RLAgent
{
    public abstract class PacManBaseGymAgent : GymAgent
    {
        [SerializeField]
        private int physicsStepsPerGymStep = 10;

        protected PacManAgentManager Agent { get; private set; }
        protected AgentCoordinator Coordinator { get; private set; }
        protected Vector2 CurrentAction { get; private set; } = Vector2.zero;
        protected float RewardAccumulator { get; set; }
        protected Dictionary<string, float> RewardWeights { get; } = new();

        protected virtual void Awake()
        {
            GymVecEnvManager.Instance.physicsStepsPerGymStep = Mathf.Max(1, physicsStepsPerGymStep);
            Agent = GetComponent<PacManAgentManager>();
        }

        protected override void Initialize()
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

        protected override void CollectInfo(Dictionary<string, float> metadata)
        {
            metadata.Add("Red", Agent.CompareTag("Red") ? 1 : 0);
            metadata.Add("Blue", Agent.CompareTag("Blue") ? 1 : 0);
        }

        protected override void GymReset()
        {
            // Debug.Log("GymReset called for " + gameObject.name + gameObject.GetInstanceID());
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
        }

        protected virtual void OnFixedStep()
        {
        }

        protected override void SetAction(AgentAction agentAction)
        {
            CurrentAction = new Vector2(agentAction.Continuous[0], agentAction.Continuous[1]);
        }

        protected override void CollectObservation(ref AgentObservation observation)
        {
            CollectAgentSpecificObservations(ref observation);
            PacManObservationCollector.CollectObservation(observation, Agent, Coordinator);
        }

        protected virtual void CollectAgentSpecificObservations(ref AgentObservation observation)
        {
        }

        protected void AppendDefaultAgentSpecificObservations(ref AgentObservation observation)
        {
            // Self state and local status.
            observation.AppendContinuous((TeamAssignmentUtil.CheckTeam(Agent.gameObject) == Team.Blue) ? 1f : 0f);
            observation.AppendContinuous(Agent.IsGhost() ? 1f : 0f);
            observation.AppendContinuous(Agent.IsScared() ? 1f : 0f);
            observation.AppendContinuous(Mathf.Clamp01(Agent.GetScore() / 100f));
            observation.AppendContinuous(Mathf.Clamp01(Agent.GetCarriedFoodCount() / 10f));

            AppendFoodEntityObservations(ref observation);
            AppendHomeObservations(ref observation);
        }

        protected void AppendFoodEntityObservations(ref AgentObservation observation)
        {
            // Flow cost and dir
            // Food objects
            // Nearest food distance and density
            if(Coordinator != null)
            {
                var flowDirection = Coordinator.GetFlowDirection(Agent.transform.position);
                observation.AppendContinuous(Mathf.Clamp(flowDirection.x, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(flowDirection.z, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(Coordinator.GetFlowCost(Agent.transform.position) / 30f, 0f, 1f));
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    observation.AppendContinuous(0f);
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
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(food.activeSelf ? 1f : 0f);
                observation.AppendContinuous(
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
                    observation.AppendContinuous(0f);
                }
                foodIndex++;
            }

            observation.AppendContinuous(Mathf.Clamp01(foodDensity / 10f));
            observation.AppendContinuous(closestFoodDist == float.MaxValue ? 1f : Mathf.Clamp01(closestFoodDist / 30f));
        }

        protected void AppendHomeObservations(ref AgentObservation observation)
        {
            // Navigation and return-home guidance.
            if (Coordinator != null)
            {
                var homeFlowDirection = Coordinator.GetFlowDirectionToHome(Agent.transform.position);
                observation.AppendContinuous(Mathf.Clamp(homeFlowDirection.x, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(homeFlowDirection.z, -1f, 1f));
                float distanceToHomeSafe = Coordinator.GetFlowCostToHome(Agent.transform.position);
                if (distanceToHomeSafe >= float.MaxValue)
                {
                    distanceToHomeSafe = PacManObservationCollector.GetDistanceToHomeBorder(Agent);
                }
                observation.AppendContinuous(Mathf.Clamp01(distanceToHomeSafe / 50f));
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    observation.AppendContinuous(0f);
                }
            }
        }

        protected override EnvironmentState GymStep()
        {
            if (Coordinator == null)
            {
                return EnvironmentState.Running;
            }

            if (Coordinator.timedOut)
            {
                return EnvironmentState.Truncated;
            }

            if (Coordinator.done)
            {
                return EnvironmentState.Done;
            }

            return EnvironmentState.Running;
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
