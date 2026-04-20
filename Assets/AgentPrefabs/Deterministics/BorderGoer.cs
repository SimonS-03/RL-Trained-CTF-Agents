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
        private AgentCoordinator coordinator;
        public override void Initialize(MapManager mapManager) // Ticked when all agents spawned by the network and seen properly by the client. Not the same as Start or Awake in this assignment.
        {
            _agent = GetComponent<PacManAgentManager>();
            _mapManager = mapManager;
            coordinator = getCoordinator();
            //_obstacleMap = ObstacleMap.Initialize(_mapManager, new List<GameObject>(), Vector3.one);
            // All of the calls below should also work in here. Report it as a bug if you find that some part of the observations is inaccessible during init.

        }
        
        
        private AgentCoordinator getCoordinator()
        {
            AgentCoordinator coord = null;
            // 1. Check if we actually have a parent to avoid errors
            if (transform.parent != null)
            {
                // 2. Find the sibling by name, then get its component
                Transform managerObject = transform.parent.Find("TeamManager");
            
                if (managerObject != null)
                {
                    coord = managerObject.GetComponent<AgentCoordinator>();
                    if (coord == null)
                    {
                        Debug.LogWarning("Coordinator not found within sibling!");
                    }
                }
                else
                {
                    Debug.LogWarning("Sibling not found!");
                }
            }
            else
            {
                Debug.LogWarning("No parent found!");
            }

            return coord;
        }
        
        public override PacManAction Tick() //The Tick from the network controller
        {
            Vector3 borderFlowDirection = coordinator.GetBorderFlowDirection(transform.position, gameObject.CompareTag("Blue"));
            var droneAction = new PacManAction
            {
                Acceleration = new Vector2(borderFlowDirection.x, borderFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
            };

            return droneAction;
        }
    }
}