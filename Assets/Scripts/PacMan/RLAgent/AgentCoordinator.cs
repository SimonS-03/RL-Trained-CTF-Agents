using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Scripts.Map;
using PacMan.Game;


// TODO: Particle filtering for enemy position estimation
// Classification of food into categories (Gr15)

namespace PacMan.RLAgent
{
    public class AgentCoordinator : MonoBehaviour
    {
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        [SerializeField]
        private Vector3 margin = Vector3.one;
        [SerializeField]
        private int episodeLength = 5 * 100;
        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField ff;
        private List<PacManRLAgent> agents;

        public bool isBlueTeam = true;
        private int step;
        private bool needsReset;
        public bool timedOut;
        public bool done;
        
        // Gizmos
        public bool drawFlowField = false;
        public bool drawObstacleMap = false;

        // Set PacManGameManager as the parent of this object, and find it in Awake instead to ensure it's available
        public void Awake()
        {
            PacManGameManager = GetComponentInParent<PacManGameManager>();
            if (PacManGameManager == null)
            {
                Debug.LogError("PacManGameManager not found in parent hierarchy!");
            }
            // Debug.Log(PacManGameManager.GetInstanceID()); // To ensure each duplicata has its own
        }


        public void Start()
        {
            ObstacleMap = ObstacleMap.Initialize(
                PacManGameManager.mapManager,
                new List<GameObject>(),
                cellScale,
                margin: margin
            );
            Debug.Log("Time to build flow fields...");
            float t = Time.realtimeSinceStartup;
            BuildFlowFields();
            Debug.Log("Flow fields built in " + (Time.realtimeSinceStartup - t) + " seconds");
            
            step = 0;
        }

        void FixedUpdate()
        {
            step ++;
            // Debug.Log(PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().Count);

            timedOut = step >= episodeLength;
            if(timedOut)
            {
                needsReset = true;
            }
        }

        public void ResetEpisode()
        {
            if(!needsReset) return;
            needsReset = false;

            PacManGameManager.RestartGame();

            step = 0;
        }

        private void BuildFlowFields()
        {
            Debug.Log("BuildFlowFields");
            List<GameObject> activeGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf); // Food that is currently on the ground
            // Target food (other's food)
            List<Vector3> targetFood = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == (isBlueTeam ? Team.Red : Team.Blue)).Select(obj => obj.transform.position).ToList();
            ff = FlowField.Build(ObstacleMap, targetFood);
        }

        public Vector3 GetFlowDirection(Vector3 worldPos)
        {
            if (ff == null) return Vector3.zero;
            return ff.GetDirection(worldPos);
        }

        public float GetFlowCost(Vector3 worldPos)
        {
            if (ff == null) return float.MaxValue;
            return ff.GetCost(worldPos);
        }

        public void RegisterAgent(PacManRLAgent agent)
        {
            if (agents == null)
            {
                agents = new List<PacManRLAgent>();
            }
            agents.Add(agent);
        }

        void OnDrawGizmos() {
            if(drawFlowField && ff != null) {
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    // cellToWorld.y = 0;
                    cellToWorld += Vector3.up * 0.25f;

                    var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    // var gizmoSize = new Vector3(scale.x * 0.7f, 0.005f, scale.z * 0.7f);
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetFlowCost(cellToWorld) / 30f);
                    var dir = GetFlowDirection(cellToWorld);
                    Gizmos.DrawRay(cellToWorld, dir.normalized * 0.4f);

                    // Gizmos.DrawCube(cellToWorld, gizmoSize);
                }
            }
            if(drawObstacleMap && ObstacleMap != null) {
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    cellToWorld.y = 0.01f;

                    var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    var gizmoSize = new Vector3(scale.x * 0.7f, 0.02f, scale.z * 0.7f);
                    
                    Gizmos.color = pos.Value == ObstacleMap.Traversability.Blocked ? Color.black : Color.white;
                    Gizmos.DrawCube(cellToWorld, gizmoSize);
                }
            }
        }


    }
}
