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
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        [SerializeField]
        private Vector3 margin = Vector3.one;
        

        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField ff;
        private List<PacManRLAgent> agents;
        private List<Vector3> targetFood;
        private readonly Dictionary<int, HashSet<int>> _reportedEnemyIdsByReporter = new();
        private readonly Dictionary<int, PacManAgentManager> _enemyRefsByServerIndex = new();

        public bool isBlueTeam = true;
        [SerializeField]
        private int episodeLength; // in game steps, so more than Gym steps !!
        private int step;
        private int flowMapRebuildFreq = 10;
        private bool isBuildingFlowField = false;
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

            if(step % flowMapRebuildFreq == 0)
                rebuildFlowMapIfNeeded();
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
            targetFood = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == (isBlueTeam ? Team.Red : Team.Blue)).Select(obj => obj.transform.position).ToList();
            ff = FlowField.Build(ObstacleMap, targetFood);
        }

        private void rebuildFlowMapIfNeeded()
        {
            // TODO use coroutine or multithreading
            List<GameObject> currentActiveGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf);
            List<Vector3> currentTargetFood = currentActiveGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == (isBlueTeam ? Team.Red : Team.Blue)).Select(obj => obj.transform.position).ToList();
            if(currentTargetFood.Count != targetFood.Count)
            {
                BuildFlowFields();
            }
            // else if(!new HashSet<Vector3>(currentTargetFood).SetEquals(new HashSet<Vector3>(targetFood)))
            // {
            //     BuildFlowFields();
            // }
            targetFood = currentTargetFood;
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

        public void ReportVisibleEnemies(PacManAgentManager reporter, IEnumerable<PacManAgentManager> visibleEnemies)
        {
            if (reporter == null || reporter.serverIndex < 0)
            {
                return;
            }

            if (!_reportedEnemyIdsByReporter.TryGetValue(reporter.serverIndex, out var reporterEnemyIds))
            {
                reporterEnemyIds = new HashSet<int>();
                _reportedEnemyIdsByReporter[reporter.serverIndex] = reporterEnemyIds;
            }
            reporterEnemyIds.Clear();

            if (visibleEnemies == null)
            {
                return;
            }

            foreach (var enemy in visibleEnemies)
            {
                if (enemy == null || !enemy.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (enemy.serverIndex >= 0)
                {
                    reporterEnemyIds.Add(enemy.serverIndex);
                    _enemyRefsByServerIndex[enemy.serverIndex] = enemy;
                }
            }
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
