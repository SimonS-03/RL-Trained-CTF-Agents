using System.Collections.Generic;
using System.Linq;
using PacMan.Interface.PacMan;
using PacMan.Local;
using PacMan.RLAgent;
using Scripts.Map;
using UnityEngine;

namespace PacMan.Agent
{
    public class BorderGoer : PacManAI
    {
        
        public override void Initialize(MapManager mapManager) // Ticked when all agents spawned by the network and seen properly by the client. Not the same as Start or Awake in this assignment.
        {
            _agent = GetComponent<PacManAgentManager>();
            _mapManager = mapManager;
            //_obstacleMap = ObstacleMap.Initialize(_mapManager, new List<GameObject>(), Vector3.one);
            // All of the calls below should also work in here. Report it as a bug if you find that some part of the observations is inaccessible during init.
        }
        
        
        public override PacManAction Tick() //The Tick from the network controller
        {
            var coordinator = gameObject.CompareTag("Blue") ? AgentCoordinator.BlueInstance : AgentCoordinator.RedInstance;
            Vector3 borderFlowDirection = coordinator != null
                ? coordinator.GetBorderFlowDirection(transform.position)
                : Vector3.zero;
            var droneAction = new PacManAction
            {
                Acceleration = new Vector2(borderFlowDirection.x, borderFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
            };

            return droneAction;
        }
    }
}