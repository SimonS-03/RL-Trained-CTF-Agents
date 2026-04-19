using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Scripts.Map;
using PacMan.Game;
using PacMan.Local;
using PacMan.Interface.PacMan;

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
        public struct AggregatedEnemyObservation
        {
            public int ServerIndex;
            public Vector3 Position;
            public bool Visible;
            public int ReporterCount;
        }

        public static AgentCoordinator BlueInstance;
        public static AgentCoordinator RedInstance;
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        [SerializeField]
        private Vector3 margin = Vector3.one;
        
        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField foodFlow, borderFlow, capsuleFlow;
        public List<Vector3> targetFood = new List<Vector3>(), allFood = new List<Vector3>();
        public List<Vector3> targetCapsules = new List<Vector3>(), allCapsules = new List<Vector3>();
        private readonly Dictionary<int, HashSet<int>> _reportedEnemyIdsByReporter = new();
        private readonly Dictionary<int, PacManAgentManager> _enemyRefsByServerIndex = new();
        private readonly Dictionary<int, Dictionary<int, PacManObservation>> _latestReportsByReporter = new();
        private readonly Dictionary<int, float> _lastReportTimeByReporter = new();
        private readonly Dictionary<int, Vector3> _lastKnownEstimatedEnemyPosById = new();

        private int step;
        [SerializeField] 
        private int flowMapRebuildFreq = 10;
        [SerializeField]
        private float reportStaleAfterSeconds = 2.5f;
        [SerializeField]
        public bool isBlueTeam = true;
        
        // Gizmos
        public bool drawFlowField = false;
        public bool drawObstacleMap = false;
        public bool drawBorderFlow = false;
        public bool drawEnemyReports = false;

        // Set PacManGameManager as the parent of this object, and find it in Awake instead to ensure it's available
        public void Awake()
        {
            if(isBlueTeam)
            {
                if(BlueInstance != null && BlueInstance != this)
                    Destroy(this.gameObject);
                else
                {
                    BlueInstance = this;
                }
            }
            else
            {
                if(RedInstance != null && RedInstance != this)
                    Destroy(this.gameObject);
                else
                {
                    RedInstance = this;
                }
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
            buildFoodFlowFields();
            buildCapsuleFlowFields();
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

        private void buildFoodFlowFields(List<GameObject> activeGoals = null)
        {
            if (activeGoals == null)
                activeGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf);
            Debug.Log("BuildFlowFields");
            var targetTeam = isBlueTeam ? Team.Red : Team.Blue;

            // Each coordinator only tracks goals relevant to its own team.
            targetFood = activeGoals.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == targetTeam)
                .Select(obj => obj.transform.position)
                .ToList();

            Debug.Log($"targetFood ({(isBlueTeam ? "Blue" : "Red")}): {targetFood.Count}");
            foodFlow = FlowField.Build(ObstacleMap, targetFood);
        }

        private void buildCapsuleFlowFields(List<GameObject> activeCapsules = null)
        {
            if (activeCapsules == null)
                activeCapsules = PacManGameManager.capsules.FindAll(capsule => capsule.activeSelf);
            
            allCapsules = activeCapsules.Select(obj => obj.transform.position).ToList();
            var targetTeam = isBlueTeam ? Team.Red : Team.Blue;

            targetCapsules = activeCapsules.Where(obj => TeamAssignmentUtil.CheckTeam(obj) == targetTeam)
                .Select(obj => obj.transform.position)
                .ToList();

            capsuleFlow = FlowField.Build(ObstacleMap, targetCapsules);
        }

        private void rebuildFlowMapIfNeeded()
        {
            // TODO use coroutine or multithreading
            List<GameObject> currentActiveGoals = PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().FindAll(food => food.activeSelf);
            List<Vector3> currentTargetFood = currentActiveGoals.Select(obj => obj.transform.position).ToList();
            if(currentTargetFood.Count != allFood.Count) // If somebody picked up some food, recalculate!
                buildFoodFlowFields(currentActiveGoals);
            
            List<GameObject> currentActiveCapsules = PacManGameManager.capsules.FindAll(capsule => capsule.activeSelf);
            if (currentActiveCapsules.Count != allCapsules.Count)
                buildCapsuleFlowFields(currentActiveCapsules);
            
            
            allFood = currentTargetFood;
        }

        private void buildBorderFlow()
        {
            // float sampleStep = .5f;
            // Vector3 minBounds = PacManGameManager.mapManager.transform.TransformPoint(ObstacleMap.cellBounds.min),
            //     maxBounds = PacManGameManager.mapManager.transform.TransformPoint(ObstacleMap.cellBounds.max);
            // List<Vector3> borderPointsBlue = new List<Vector3>(), borderPointsRed = new List<Vector3>();
            // for (float z = minBounds.z; z <= maxBounds.z; z += sampleStep)
            // {
            //     borderPointsBlue.Add(new Vector3(-.5f, 0, z));
            //     borderPointsRed.Add(new Vector3(.5f, 0, z));
            // }
            List<Vector3> borderPoints = new List<Vector3>();
            foreach (var cell in ObstacleMap.traversabilityPerCell)
            {
                if (cell.Value != ObstacleMap.Traversability.Blocked)
                {
                    var worldPos = ObstacleMap.CellToWorld(new Vector3Int(cell.Key.x, 0, cell.Key.y)) + ObstacleMap.trueScale / 2;
                    if (isBlueTeam && worldPos.x < -1f)
                        borderPoints.Add(worldPos);
                    if (!isBlueTeam && worldPos.x > 1f)
                        borderPoints.Add(worldPos);
                }
            }
            borderFlow = FlowField.Build(ObstacleMap, borderPoints);
        }

        public Vector3 GetCapsuleFlowDirection(Vector3 worldPos)
        {
            if (capsuleFlow == null) return Vector3.zero;
            return capsuleFlow.GetDirection(worldPos);
        }

        public Vector3 GetFoodFlowDirection(Vector3 worldPos)
        {
            if (foodFlow == null) return Vector3.zero;
            return foodFlow.GetDirection(worldPos);
        }

        public float GetFoodFlowCost(Vector3 worldPos)
        {
            if (foodFlow == null) return float.MaxValue;
            return foodFlow.GetCost(worldPos);
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

        public void ReportEnemyObservations(PacManAgentManager reporter, PacManObservations observations)
        {
            if (reporter == null)
            {
                return;
            }

            int reporterId = reporter.serverIndex;
            if (reporterId < 0)
            {
                return;
            }

            _lastReportTimeByReporter[reporterId] = Time.time;

            if (!_latestReportsByReporter.TryGetValue(reporterId, out var reporterObservations))
            {
                reporterObservations = new Dictionary<int, PacManObservation>();
                _latestReportsByReporter[reporterId] = reporterObservations;
            }
            else
            {
                reporterObservations.Clear();
            }

            if (!_reportedEnemyIdsByReporter.TryGetValue(reporterId, out var reportedEnemyIds))
            {
                reportedEnemyIds = new HashSet<int>();
                _reportedEnemyIdsByReporter[reporterId] = reportedEnemyIds;
            }
            else
            {
                reportedEnemyIds.Clear();
            }

            var observationArray = observations.Observations ?? System.Array.Empty<PacManObservation>();
            foreach (var observation in observationArray)
            {
                if (observation.ServerIndex < 0)
                {
                    continue;
                }

                reporterObservations[observation.ServerIndex] = observation;

                if (observation.Visible)
                {
                    reportedEnemyIds.Add(observation.ServerIndex);
                }
            }

            // Integrate immediate line-of-sight observations as ground truth.
            // These are not delayed and should override noisy approximations for matching enemies.
            var visibleEnemies = reporter.GetVisibleEnemyAgents();
            foreach (var enemy in visibleEnemies)
            {
                if (enemy == null || enemy.serverIndex < 0)
                {
                    continue;
                }

                reporterObservations[enemy.serverIndex] = new PacManObservation
                {
                    ServerIndex = enemy.serverIndex,
                    Visible = true,
                    Position = enemy.transform.localPosition,
                    IsGhost = enemy.isGhost,
                    HasFood = enemy.GetCarriedFoodCount() > 0,
                    ReadingDispersion = 0f,
                    LastRespawnStep = enemy.GetLastRespawnStep()
                };
                reportedEnemyIds.Add(enemy.serverIndex);
            }
        }

        public List<AggregatedEnemyObservation> GetAggregatedEnemyObservations()
        {
            PruneStaleReports();

            var groupedByEnemy = new Dictionary<int, List<PacManObservation>>();
            foreach (var reporterKvp in _latestReportsByReporter)
            {
                foreach (var enemyKvp in reporterKvp.Value)
                {
                    if (!groupedByEnemy.TryGetValue(enemyKvp.Key, out var enemyReports))
                    {
                        enemyReports = new List<PacManObservation>();
                        groupedByEnemy[enemyKvp.Key] = enemyReports;
                    }

                    enemyReports.Add(enemyKvp.Value);
                }
            }

            var aggregated = new List<AggregatedEnemyObservation>();
            foreach (var enemyKvp in groupedByEnemy)
            {
                int enemyId = enemyKvp.Key;
                var enemyReports = enemyKvp.Value;

                var visibleReports = enemyReports.Where(obs => obs.Visible).ToList();
                var noisyReports = enemyReports
                    .Where(obs => !obs.Visible && obs.Position.sqrMagnitude > 0.0001f)
                    .ToList();

                Vector3 estimatedPosition;
                bool hasEstimate = false;
                bool isVisible = visibleReports.Count > 0;

                if (isVisible)
                {
                    estimatedPosition = AveragePosition(visibleReports.Select(obs => obs.Position));
                    hasEstimate = true;
                }
                else if (noisyReports.Count > 0)
                {
                    // Approximate latent enemy position from all non-visible noisy readings.
                    estimatedPosition = AveragePosition(noisyReports.Select(obs => obs.Position));
                    hasEstimate = true;
                }
                else if (_lastKnownEstimatedEnemyPosById.TryGetValue(enemyId, out var lastEstimate))
                {
                    estimatedPosition = lastEstimate;
                    hasEstimate = true;
                }
                else
                {
                    continue;
                }

                if (hasEstimate)
                {
                    _lastKnownEstimatedEnemyPosById[enemyId] = estimatedPosition;
                    aggregated.Add(new AggregatedEnemyObservation
                    {
                        ServerIndex = enemyId,
                        Position = estimatedPosition,
                        Visible = isVisible,
                        ReporterCount = enemyReports.Count
                    });
                }
            }

            return aggregated;
        }

        private void PruneStaleReports()
        {
            if (_lastReportTimeByReporter.Count == 0)
            {
                return;
            }

            var staleReporterIds = _lastReportTimeByReporter
                .Where(kvp => Time.time - kvp.Value > reportStaleAfterSeconds)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var reporterId in staleReporterIds)
            {
                _lastReportTimeByReporter.Remove(reporterId);
                _latestReportsByReporter.Remove(reporterId);
                _reportedEnemyIdsByReporter.Remove(reporterId);
            }
        }

        private static Vector3 AveragePosition(IEnumerable<Vector3> positions)
        {
            int count = 0;
            Vector3 sum = Vector3.zero;
            foreach (var pos in positions)
            {
                sum += pos;
                count++;
            }

            return count == 0 ? Vector3.zero : sum / count;
        }

        private Vector3 ObservationToWorldPosition(Vector3 observationPosition)
        {
            if (PacManGameManager == null)
            {
                return observationPosition;
            }

            return PacManGameManager.transform.TransformPoint(observationPosition);
        }

        void OnDrawGizmos() {
            if(drawFlowField && foodFlow != null) {
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    // cellToWorld.y = 0;
                    cellToWorld += Vector3.up * 0.25f;

                    var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    // var gizmoSize = new Vector3(scale.x * 0.7f, 0.005f, scale.z * 0.7f);
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetFoodFlowCost(cellToWorld) / 30f);
                    var dir = GetFoodFlowDirection(cellToWorld);
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

            if (drawEnemyReports)
            {
                // Raw reported enemy positions from each teammate report.
                foreach (var reporterReports in _latestReportsByReporter.Values)
                {
                    foreach (var report in reporterReports.Values)
                    {
                        if (!report.Visible && report.Position.sqrMagnitude <= 0.0001f)
                        {
                            continue;
                        }

                        var worldPos = ObservationToWorldPosition(report.Position) + Vector3.up * 0.05f;
                        Gizmos.color = report.Visible ? Color.yellow : Color.cyan;
                        Gizmos.DrawWireSphere(worldPos, 0.25f);
                    }
                }

                // Fused estimate per enemy id.
                var aggregated = GetAggregatedEnemyObservations();
                foreach (var enemy in aggregated)
                {
                    var worldPos = ObservationToWorldPosition(enemy.Position) + Vector3.up * 0.05f;
                    Gizmos.color = enemy.Visible ? Color.red : new Color(1f, 0.35f, 0.1f);
                    Gizmos.DrawSphere(worldPos, 0.18f);
                }
            }
        }


    }
}
