using System;
using System.Collections.Generic;
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
        private PacManAgentManager _agent;
        private PacManMovementController _controller;
        private bool _caughtTarget;
        private Vector2 _currentAction = Vector2.zero;
        private PacManAgentManager _target;
        private float _distancePrevious;

        private void Awake()
        {
            GymVecEnvManager.Instance.physicsStepsPerGymStep = 5;
            _agent = GetComponent<PacManAgentManager>();
        }

        protected override void Initialize()
        {
        }

        protected override void CollectInfo(Dictionary<string, float> metadata)
        {
            metadata.Add("Red", _agent.CompareTag("Red") ? 1 : 0); // Just some proverbial food for thought
            metadata.Add("Blue", _agent.CompareTag("Blue") ? 1 : 0);
        }

        protected override void GymReset()
        {
            var localRandomPosition = new Vector3(Random.Range(-13f, -2.5f), 0.4f, Random.Range(-5f, 5f));
            _agent.transform.position = _agent.PacManGameManager.transform.TransformPoint(localRandomPosition);
            _caughtTarget = false;

            _target = _agent.PacManGameManager.agents.Find(agent => agent.gameObject != gameObject);

            localRandomPosition = new Vector3(Random.Range(-13f, -2.5f), 0.4f, Random.Range(-5f, 5f));
            _target.transform.position = _agent.PacManGameManager.transform.TransformPoint(localRandomPosition);
            _distancePrevious = DistanceToTarget(_target, _agent).magnitude;
        }

        public void FixedUpdate()
        {
            _agent.Movement.ApplyDesiredControl(_currentAction);
        }

        protected override void SetAction(AgentAction agentAction)
        {
            _currentAction = new Vector2(agentAction.Continuous[0], agentAction.Continuous[1]);
        }

        protected override void CollectObservation(ref AgentObservation observation)
        {
            CollectObservation(observation, _target, _agent);
        }

        public static AgentObservation CollectObservation(AgentObservation observation, PacManAgentManager targetAgent, PacManAgentManager thisAgent)
        {
            // Observation ideas
            // - Flow field direction and score
            // - On ennemy terrain
            // - NearbyAssets/Scripts/PacMan/RLExample/PacManAIRL.cs agents position
            // - raycasts x8
            // - Ghost or not ?
            // - Score and food count
            // - 


            var enemyRelative = DistanceToTarget(targetAgent, thisAgent); //Normalized target vector. Not in agent space, because actions are not in agent space :)
            observation.AppendContinuous(Mathf.Clamp(enemyRelative.x, -1f, 1f));
            observation.AppendContinuous(Mathf.Clamp(enemyRelative.z, -1f, 1f));

            var enemyVelocity = targetAgent.GetComponent<Rigidbody>().linearVelocity / 2.1f;
            observation.AppendContinuous(Mathf.Clamp(enemyVelocity.x, -1, 1));
            observation.AppendContinuous(Mathf.Clamp(enemyVelocity.z, -1, 1));
            return observation;

            // Available :

            thisAgent.GetTimeRemaining();
            thisAgent.GetScore();
            bool isGhost = thisAgent.IsGhost();
            bool isScared = thisAgent.IsScared();
            float scaredDuration = thisAgent.GetScaredRemainingDuration();

            float carriedFoodCount = thisAgent.GetCarriedFoodCount();

            List<GameObject> foodPositions = thisAgent.GetFoodObjects(); // Positions of food or last know position of food
            var activeFoodPositions = foodPositions.FindAll(food => food.activeSelf); // Food that is currently on the ground
            var inactiveFoodLatestPositions = foodPositions.FindAll(food => !food.activeSelf); // Food that is currently carried. The gameObject position will report where it was picked up from. Might be useful in some scenarios.
            List<GameObject> capsulePositions = thisAgent.GetCapsuleObjects();

            // var isLocalPointTraversable = _obstacleMap?.GetLocalPointTraversibility(transform.localPosition);

            var teamAgentManagers = thisAgent.GetTeamAgents(); //Agents in team, including this agent
            var friendlyAgentManagers = thisAgent.GetFriendlyAgents(); //Agents in team, except this agent

            var visibleEnemyAgents = thisAgent.GetVisibleEnemyAgents(); // Enemy agents in LoS. Know percise information
            PacManObservations fetchEnemyObservations = thisAgent.GetEnemyObservations(); // Enemies out of LoS. Know partial information. 
            if (fetchEnemyObservations.Observations.Length > 0)
            {
                // Debug.Log(fetchEnemyObservations.ObservationFixedTime);
            }

            // Since the RigidBody is updated server side and the client only syncs position, rigidbody.Velocity does not report a velocity
            var velocity = thisAgent.GetVelocity(); // Use the manager method to get the true velocity from the server
            // friendlyAgentManager.GetVelocity(); // Given the damping, max velocity magnitude is around 2.34
        }

        public static Vector3 DistanceToTarget(PacManAgentManager targetAgent, PacManAgentManager agent)
        {
            return (targetAgent.transform.position - agent.transform.position) / 15f;
        }

        protected override float CollectReward()
        {
            if (_caughtTarget) return 0.75f;
            var distanceCurrent = DistanceToTarget(_target, _agent).magnitude;
            var potentialReward = _distancePrevious - distanceCurrent;
            return potentialReward / gymSteps * 0.25f;
        }

        protected override EnvironmentState GymStep()
        {
            if (_caughtTarget) return EnvironmentState.Done;
            return EnvironmentState.Running;
        }

        public virtual void OnCollisionStay(Collision other)
        {
            if (other.gameObject.name == "PacManBrownian" && other.gameObject.tag != tag)
            {
                _caughtTarget = true;
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