using System.Collections.Generic;
using System.Linq;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Scripts.Map;
using UnityEngine;

namespace PacMan.Agent
{
    public class PacManAI : MonoBehaviour
    {
        protected PacManAgentManager _agent;
        protected ObstacleMap _obstacleMap;
        protected MapManager _mapManager;

        public virtual void Initialize(MapManager mapManager) // Ticked when all agents spawned by the network and seen properly by the client. Not the same as Start or Awake in this assignment.
        {
            _agent = GetComponent<PacManAgentManager>();
            _mapManager = mapManager;
            _obstacleMap = ObstacleMap.Initialize(_mapManager, new List<GameObject>(), Vector3.one);
            // All of the calls below should also work in here. Report it as a bug if you find that some part of the observations is inaccessible during init.
        }

        public virtual PacManAction Tick() //The Tick from the network controller
        {
            _agent.GetTimeRemaining();
            var stepsSinceMatchStart = _agent.GetStepsSinceMatchStart();
            var stepsRemaining = _agent.GetStepsRemaining();
            var score = _agent.GetScore();
            var lastRespawnStep = _agent.GetLastRespawnStep();
            bool isGhost = _agent.IsGhost();
            bool isScared = _agent.IsScared();
            float scaredDuration = _agent.GetScaredRemainingDuration();

            float carriedFoodCount = _agent.GetCarriedFoodCount();

            List<GameObject> foodPositions = _agent.GetFoodObjects(); // Positions of food or last know position of food
            var activeFoodPositions = foodPositions.FindAll(food => food.activeSelf); // Food that is currently on the ground
            var inactiveFoodLatestPositions = foodPositions.FindAll(food => !food.activeSelf); // Food that is currently carried. The gameObject position will report where it was picked up from. Might be useful in some scenarios.
            List<GameObject> capsulePositions = _agent.GetCapsuleObjects();

            var isLocalPointTraversable = _obstacleMap?.GetLocalPointTraversibility(transform.localPosition);

            var teamAgentManagers = _agent.GetTeamAgents(); //Agents in team, including this agent
            var friendlyAgentManagers = _agent.GetFriendlyAgents(); //Agents in team, except this agent

            var visibleEnemyAgents = _agent.GetVisibleEnemyAgents(); // Enemy agents in LoS. Know percise information
            PacManObservations fetchEnemyObservations = _agent.GetEnemyObservations(); // Enemies out of LoS. Know partial information. 
            if (fetchEnemyObservations.Observations.Length > 0)
            {
                // Debug.Log(fetchEnemyObservations.ObservationFixedTime);
            }

            // Since the RigidBody is updated server side and the client only syncs position, rigidbody.Velocity does not report a velocity
            var velocity = _agent.GetVelocity(); // Use the manager method to get the true velocity from the server
            // friendlyAgentManager.GetVelocity(); // Given the damping, max velocity magnitude is around 2.34
            if (lastRespawnStep != 0 && lastRespawnStep == stepsSinceMatchStart)
            {
                print("Detected respawn step");
            }
            // // replace the human input below with some AI stuff
            var x = 0;
            var z = 0;

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("w") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("up"))
            {
                z = 1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("a") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("left"))
            {
                x = -1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("s") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("down"))
            {
                z = -1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("d") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("right"))
            {
                x = 1;
            }

            var droneAction = new PacManAction
            {
                Acceleration = new Vector2(x, z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
            };

            return droneAction;
        }
    }
}
