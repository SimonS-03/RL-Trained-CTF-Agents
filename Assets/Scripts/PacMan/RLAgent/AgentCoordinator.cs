using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Scripts.Map;
using PacMan.Game;
using PacMan.Local;
using System.Threading.Tasks;
using Scripts.VecEnv.Core;

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
        // Track reset frame per game-manager instance so duplicated environments
        // can reset independently while still deduplicating per-env multi-agent resets.
        private static readonly Dictionary<int, int> LastResetFrameByGameManager = new();
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        [SerializeField]
        private Vector3 margin = Vector3.one;
        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField ff;
        private FlowField ffHome;
        private List<GymAgent> agents;
        private List<Vector3> targetFood;
        private readonly Dictionary<int, HashSet<int>> _reportedEnemyIdsByReporter = new();
        private readonly Dictionary<int, PacManAgentManager> _enemyRefsByServerIndex = new();

        public bool isBlueTeam = true;
        public bool trainingAttackers = true;
        public bool trainingAttackDefense = false;
        public bool spawnOnBlueSide = true;
        [SerializeField]
        private int episodeLength; // in game steps, so more than Gym steps !!
        private int step;
        private int flowMapRebuildFreq = 10;
        private bool needsReset;
        public bool timedOut;
        public bool done;

        private List<Vector3> redSpawnPositions;
        private List<Vector3> blueSpawnPositions;
        
        // Gizmos
        public bool drawFlowField = false;
        public bool drawObstacleMap = false;

        // Resolve the manager from hierarchy once.
        public void Awake()
        {
            PacManGameManager = GetComponentInParent<PacManGameManager>();
            if (PacManGameManager == null)
            {
                Debug.LogError("AgentCoordinator: PacManGameManager not found in parent hierarchy.");
            }
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
            RebuildTrainingState();
            Debug.Log("Flow fields built in " + (Time.realtimeSinceStartup - t) + " seconds");
            
            step = 0;
        }

        private void OnDestroy()
        {
            if (PacManGameManager == null)
            {
                return;
            }

            LastResetFrameByGameManager.Remove(PacManGameManager.GetInstanceID());
        }

        public void ConfigureTraining(bool isBlueTeam, bool trainingAttackers, bool trainingAttackDefense, bool spawnOnBlueSide)
        {
            this.isBlueTeam = isBlueTeam;
            this.trainingAttackers = trainingAttackers;
            this.trainingAttackDefense = trainingAttackDefense;
            this.spawnOnBlueSide = spawnOnBlueSide;

            if (ObstacleMap != null)
            {
                RebuildTrainingState();
            }
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
            if(!needsReset && !trainingAttackDefense) return;

            if (PacManGameManager == null)
            {
                return;
            }

            // Gym reset can be invoked once per registered RL agent in the same frame.
            // Deduplicate by environment instance, not globally across all duplicated maps.
            var gameManagerId = PacManGameManager.GetInstanceID();
            if (LastResetFrameByGameManager.TryGetValue(gameManagerId, out var lastResetFrame) && lastResetFrame == Time.frameCount)
            {
                return;
            }
            LastResetFrameByGameManager[gameManagerId] = Time.frameCount;

            needsReset = false;

            PacManGameManager.RestartGame();

            if(
                trainingAttackDefense &&
                blueSpawnPositions != null &&
                blueSpawnPositions.Count > 0 &&
                redSpawnPositions != null &&
                redSpawnPositions.Count > 0
            )
            {
                var sideSpawnPositions = spawnOnBlueSide ? blueSpawnPositions : redSpawnPositions;
                if (sideSpawnPositions == null || sideSpawnPositions.Count == 0)
                {
                    step = 0;
                    return;
                }

                var allManagers = PacManGameManager != null && PacManGameManager.agents != null
                    ? PacManGameManager.agents
                    : new List<PacManAgentManager>();
                foreach(var manager in allManagers)
                {
                    if(manager == null)
                    {
                        continue;
                    }

                    manager.transform.position = sideSpawnPositions[Random.Range(0, sideSpawnPositions.Count)];
                }
            }

            step = 0;
        }

        private void BuildFlowFields()
        {
            // Debug.Log("BuildFlowFields");
            List<GameObject> activeGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf); // Food that is currently on the ground
            // Target food (other's food)
            targetFood = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == (isBlueTeam ? Team.Red : Team.Blue)).Select(obj => obj.transform.position).ToList();
            ff = FlowField.Build(ObstacleMap, targetFood);
        }

        private void BuildFlowMapToHome()
        {
            List<Vector3> homeBorderPositions = new List<Vector3>();
            foreach(var cell in ObstacleMap.traversabilityPerCell)
            {
                var worldPos = ObstacleMap.CellToWorld(new Vector3Int(cell.Key.x, 0, cell.Key.y)) + ObstacleMap.trueScale / 2;
                var localPos = PacManGameManager.transform.InverseTransformPoint(worldPos);
                if((isBlueTeam && localPos.x < 0) || (!isBlueTeam && localPos.x > 0))
                {
                    homeBorderPositions.Add(worldPos);
                }
            }
            ffHome = FlowField.Build(ObstacleMap, homeBorderPositions);
        }

        private void RebuildTrainingState()
        {
            BuildFlowFields();
            BuildFlowMapToHome();

            if(!trainingAttackDefense)
            {
                redSpawnPositions = null;
                blueSpawnPositions = null;
                return;
            }

            // generate all possible red and blue spawn positions based on the obstaclemap free cells
            redSpawnPositions = new List<Vector3>();
            blueSpawnPositions = new List<Vector3>();
            foreach(var cell in ObstacleMap.traversabilityPerCell)
            {
                if(cell.Value != ObstacleMap.Traversability.Free)
                {
                    continue;
                }

                var worldPos = ObstacleMap.CellToWorld(new Vector3Int(cell.Key.x, 0, cell.Key.y)) + ObstacleMap.trueScale / 2;
                var localPos = PacManGameManager.transform.InverseTransformPoint(worldPos) - Vector3.up * 0.1f; 
                if(localPos.x > 0)
                {
                    redSpawnPositions.Add(worldPos);
                }
                else
                {
                    blueSpawnPositions.Add(worldPos);
                }
            }
        }

        public void RespawnAgentRandomly(GymAgent agent)
        {
            var sideSpawnPositions = spawnOnBlueSide ? blueSpawnPositions : redSpawnPositions;

            var randomSpawn = sideSpawnPositions[Random.Range(0, sideSpawnPositions.Count)];
            agent.transform.position = randomSpawn;
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

        public Vector3 GetFlowDirectionToHome(Vector3 worldPos)
        {
            if (ffHome == null) return Vector3.zero;
            return ffHome.GetDirection(worldPos);
        }

        public float GetFlowCostToHome(Vector3 worldPos)
        {
            if (ffHome == null) return float.MaxValue;
            return ffHome.GetCost(worldPos);
        }

        public void RegisterAgent(GymAgent agent)
        {
            if (agents == null)
            {
                agents = new List<GymAgent>();
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
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetFlowCostToHome(cellToWorld) / 30f);
                    var dir = GetFlowDirectionToHome(cellToWorld);
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
