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
        protected PacManAction _currentAction;

        public virtual void Initialize(MapManager mapManager) // Ticked when all agents spawned by the network and seen properly by the client. Not the same as Start or Awake in this assignment.
        {
            _agent = GetComponent<PacManAgentManager>();
            _mapManager = mapManager;
            _obstacleMap = ObstacleMap.Initialize(_mapManager, new List<GameObject>(), Vector3.one);
            // All of the calls below should also work in here. Report it as a bug if you find that some part of the observations is inaccessible during init.
        
            _currentAction = new PacManAction
            {
                Acceleration = new Vector2(0f, 0f), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
            };
        }
        
        public virtual PacManAction Tick() //The Tick from the network controller
        {
            return _currentAction;
        }

        public void setAction(PacManAction action)
        {
            _currentAction = action;
        }
    }
}