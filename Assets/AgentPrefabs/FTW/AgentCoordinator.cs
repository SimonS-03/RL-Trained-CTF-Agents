using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Scripts.Map;
using PacMan.Game;
using PacMan.Local;
using System.Threading.Tasks;

// TODO: Particle filtering for enemy position estimation
// Classification of food into categories (Gr15)
// Weight randomization and input to the agent so that it specialises
// Outputting what the agent is doing (reward based on how much time it said it) and feeding to the others
// Population
// 1 flow map / food ?
// How do we choose the weight inputs for the final agent ? (the role)
// LSTM memory

namespace PacMan.RLAgent
{
    public class AgentCoordinator : MonoBehaviour
    {
        public static AgentCoordinator Instance;
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        [SerializeField]
        private Vector3 margin = Vector3.one;
        

        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField foodFlowBlue, foodFlowRed, borderFlow;
        public List<Vector3> targetFoodBlue = new List<Vector3>(), targetFoodRed = new List<Vector3>(), allFood = new List<Vector3>();
        private readonly Dictionary<int, HashSet<int>> _reportedEnemyIdsByReporter = new();
        private readonly Dictionary<int, PacManAgentManager> _enemyRefsByServerIndex = new();

        [SerializeField]
        private int episodeLength; // in game steps, so more than Gym steps !!
        private int step;
        [SerializeField] private int flowMapRebuildFreq = 10;
        private bool isBuildingFlowField = false;
        private bool needsReset;
        public bool timedOut;
        public bool done;
        
        // Gizmos
        public bool drawFlowField = false;
        public bool drawObstacleMap = false;
        public bool drawBorderFlow = false;

        // Set PacManGameManager as the parent of this object, and find it in Awake instead to ensure it's available
        public void Awake()
        {
            if(Instance != null && Instance != this)
                Destroy(this.gameObject);
            else
            {
                Instance = this;
            }
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
            float t = Time.realtimeSinceStartup;
            BuildFlowFields();
            buildBorderFlow();
            Debug.Log("Flow fields built in " + (Time.realtimeSinceStartup - t) + " seconds");
            
            step = 0;
        }

        void FixedUpdate()
        {
            step ++;
            // Debug.Log(PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().Count);


            if(step % flowMapRebuildFreq == 0)
                rebuildFlowMapIfNeeded();
        }

        private void BuildFlowFields(List<GameObject> activeGoals = null)
        {
            if (activeGoals == null)
                activeGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf);
            Debug.Log("BuildFlowFields");
            // Target food (other's food)
            targetFoodBlue = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == Team.Red).Select(obj => obj.transform.position).ToList();
            targetFoodRed = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == Team.Blue).Select(obj => obj.transform.position).ToList();
            
            foodFlowBlue = FlowField.Build(ObstacleMap, targetFoodBlue);
            foodFlowRed = FlowField.Build(ObstacleMap, targetFoodRed);
        }

        private void rebuildFlowMapIfNeeded()
        {
            // TODO use coroutine or multithreading
            List<GameObject> currentActiveGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf);
            List<Vector3> currentTargetFood = currentActiveGoals.Select(obj => obj.transform.position).ToList();
            
            if(currentTargetFood.Count != allFood.Count) // If somebody picked up some food, recalculate!
            {
                BuildFlowFields(currentActiveGoals);
            }
            // else if(!new HashSet<Vector3>(currentTargetFood).SetEquals(new HashSet<Vector3>(targetFood)))
            // {
            //     BuildFlowFields();
            // }
            allFood = currentTargetFood;
        }

        private void buildBorderFlow()
        {
            float sampleStep = .5f;
            Vector3 minBounds = PacManGameManager.mapManager.transform.TransformPoint(ObstacleMap.cellBounds.min),
                maxBounds = PacManGameManager.mapManager.transform.TransformPoint(ObstacleMap.cellBounds.max);
            List<Vector3> borderPoints = new List<Vector3>();
            for (float z = minBounds.z; z <= maxBounds.z; z += sampleStep)
            {
                borderPoints.Add(new Vector3(0, 0, z));
            }
            
            borderFlow = FlowField.Build(ObstacleMap, borderPoints);
        }
        public Vector3 GetFoodFlowDirection(Vector3 worldPos, bool isBlue)
        {
            if (foodFlowBlue == null || foodFlowRed == null) return Vector3.zero;
            return isBlue? foodFlowBlue.GetDirection(worldPos) : foodFlowRed.GetDirection(worldPos);
        }

        public float GetFoodFlowCost(Vector3 worldPos, bool isBlue)
        {
            if (foodFlowBlue == null || foodFlowRed == null) return float.MaxValue;
            return  isBlue? foodFlowBlue.GetCost(worldPos) : foodFlowRed.GetCost(worldPos);
        }
        
        public Vector3 GetBorderFlowDirection(Vector3 worldPos)
        {
            if (borderFlow == null) return Vector3.zero;
            return borderFlow.GetDirection(worldPos);
        }

        public float GetBorderFlowCost(Vector3 worldPos)
        {
            if (borderFlow == null) return float.MaxValue;
            return borderFlow.GetCost(worldPos);
        }
        
        public List<PacManAgentManager> GetSharedVisibleEnemies()
        {
            var sharedEnemyIds = new HashSet<int>();
            foreach (var reportedIds in _reportedEnemyIdsByReporter.Values)
            {
                foreach (var enemyId in reportedIds)
                {
                    sharedEnemyIds.Add(enemyId);
                }
            }

            var visibleEnemies = new List<PacManAgentManager>();
            foreach (var enemyId in sharedEnemyIds)
            {
                if (!_enemyRefsByServerIndex.TryGetValue(enemyId, out var enemyRef))
                {
                    continue;
                }

                if (enemyRef == null || !enemyRef.gameObject.activeInHierarchy)
                {
                    _enemyRefsByServerIndex.Remove(enemyId);
                    continue;
                }

                visibleEnemies.Add(enemyRef);
            }

            return visibleEnemies;
        }

        void OnDrawGizmos() {
            if(drawFlowField && foodFlowBlue != null) {
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    // cellToWorld.y = 0;
                    cellToWorld += Vector3.up * 0.25f;

                    var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    // var gizmoSize = new Vector3(scale.x * 0.7f, 0.005f, scale.z * 0.7f);
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetFoodFlowCost(cellToWorld, true) / 30f);
                    var dir = GetFoodFlowDirection(cellToWorld, true);
                    Gizmos.DrawRay(cellToWorld, dir.normalized * 0.4f);

                    // Gizmos.DrawCube(cellToWorld, gizmoSize);
                }
            }
            
            if(drawBorderFlow && borderFlow != null) {
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    // cellToWorld.y = 0;
                    cellToWorld += Vector3.up * 0.25f;

                    var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    // var gizmoSize = new Vector3(scale.x * 0.7f, 0.005f, scale.z * 0.7f);
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetBorderFlowCost(cellToWorld) / 30f);
                    var dir = GetBorderFlowDirection(cellToWorld);
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
