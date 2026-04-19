using System.Linq;
using System.Collections.Generic;
using PacMan.Agent;
using PacMan.Interface.PacMan;
using PacMan.Local;
using PacMan.RLAgent;
using Scripts.Map;
using Unity.InferenceEngine;
using UnityEngine;

namespace PacMan.RLAgent
{
    public class PacManRLInference : PacManAI
    {
        public enum PolicyRole
        {
            Attacker,
            Defender,
            Collect,
        }

        private Worker _attackerWorker;
        private Worker _defenderWorker;
        private AgentCoordinator coordinator;

        [SerializeField]
        private PolicyRole manualRole = PolicyRole.Attacker;

        [Header("Policy Models")]
        public ModelAsset attackerModelAsset;
        public ModelAsset defenderModelAsset;

        public override void Initialize(MapManager mapManager)
        {
            base.Initialize(mapManager);
            _agent = GetComponent<PacManAgentManager>();

            _attackerWorker = CreateWorker(attackerModelAsset, PolicyRole.Attacker);
            _defenderWorker = CreateWorker(defenderModelAsset, PolicyRole.Defender);

            // Mirror PacManRLAgent team-based coordinator lookup so observations use shared team context.
            var isBlueTeam = TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue;
            coordinator = transform.parent != null
                ? transform.parent
                    .GetComponentsInChildren<AgentCoordinator>()
                    .ToList()
                    .Find(c => c.isBlueTeam == isBlueTeam)
                : GetComponentsInParent<AgentCoordinator>()
                    .ToList()
                    .Find(c => c.isBlueTeam == isBlueTeam);

            if (coordinator == null)
            {
                Debug.LogWarning("PacManRLInference could not find AgentCoordinator for this team. Collect role will use fallback obstacle map.");
            }
        }

        public override PacManAction Tick()
        {
            var activeRole = ResolveActiveRole();
            if (activeRole == PolicyRole.Collect)
            {
                return PacManCollectAStarPolicy.ComputeAction(_agent, coordinator, _obstacleMap);
            }

            var worker = GetWorker(activeRole);
            if (worker == null)
            {
                Debug.LogWarning($"PacManRLInference has no model configured for {activeRole}; falling back to manual input.");
                return ManualFallbackAction();
            }

            var observations = CollectObservationList(activeRole);
            if (observations.Count > 0)
            {
                using var input = new Tensor<float>(new TensorShape(1, observations.Count), observations.ToArray());
                worker.Schedule(input);
                var output = worker.PeekOutput() as Tensor<float>;

                if (output != null)
                {
                    // Worker runs asynchronously; ensure output data is ready before indexing.
                    output.CompleteAllPendingOperations();

                    if (output.shape.length < 2)
                    {
                        Debug.LogWarning("PacManRLInference output tensor had fewer than 2 elements.");
                        return ManualFallbackAction();
                    }

                    return new PacManAction
                    {
                        Acceleration = new Vector2(
                            Mathf.Clamp(output[0], -1f, 1f),
                            Mathf.Clamp(output[1], -1f, 1f)
                        ),
                    };
                }
            }

            Debug.LogWarning("PacManRLInference model output was missing continuous actions; falling back to manual input.");
            return ManualFallbackAction();
        }

        private PacManAction ManualFallbackAction()
        {
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
                Acceleration = new Vector2(x, z),
            };

            return droneAction;
        }

        private Worker CreateWorker(ModelAsset asset, PolicyRole role)
        {
            if (asset == null)
            {
                return null;
            }

            var runtimeModel = ModelLoader.Load(asset);
            if (runtimeModel == null)
            {
                Debug.LogError($"PacManRLInference failed to load model for {role}.");
                return null;
            }

            return new Worker(runtimeModel, BackendType.CPU);
        }

        private Worker GetWorker(PolicyRole role)
        {
            switch (role)
            {
                case PolicyRole.Attacker:
                    return _attackerWorker;
                case PolicyRole.Defender:
                    return _defenderWorker;
                default:
                    return null;
            }
        }

        private PolicyRole ResolveActiveRole()
        {
            return manualRole;
        }

        private List<float> CollectObservationList(PolicyRole role)
        {
            switch (role)
            {
                case PolicyRole.Attacker:
                    return PacManAttackerAgent.CollectObservationList(_agent, coordinator);
                case PolicyRole.Defender:
                    return PacManDefenderAgent.CollectObservationList(_agent, coordinator);
                default:
                    return new List<float>();
            }
        }

        private void OnDestroy()
        {
            _attackerWorker?.Dispose();
            _defenderWorker?.Dispose();
            _attackerWorker = null;
            _defenderWorker = null;
        }
    }

    /// <summary>
    /// Deterministic collector policy that uses A* over the obstacle grid
    /// to move toward the closest reachable enemy food.
    /// </summary>
    public static class PacManCollectAStarPolicy
    {
        private static readonly (int dx, int dz)[] CardinalNeighbors =
        {
            (1, 0),
            (-1, 0),
            (0, 1),
            (0, -1)
        };

        public static PacManAction ComputeAction(PacManAgentManager agent, AgentCoordinator coordinator, ObstacleMap fallbackMap)
        {
            if (agent == null)
            {
                return ZeroAction();
            }

            var map = coordinator != null && coordinator.ObstacleMap != null
                ? coordinator.ObstacleMap
                : fallbackMap;
            if (map == null)
            {
                return ZeroAction();
            }

            var enemyFoodCells = GetEnemyFoodCells(agent, map);
            if (enemyFoodCells.Count == 0)
            {
                return ZeroAction();
            }

            var start = ToCell(map, agent.transform.position);
            if (!IsTraversable(map, start))
            {
                if (!TryFindNearestTraversableCell(map, start, out start))
                {
                    return ZeroAction();
                }
            }

            List<Vector2Int> bestPath = null;
            var bestPathLength = int.MaxValue;

            foreach (var target in enemyFoodCells
                .OrderBy(cell => ManhattanDistance(start, cell))
                .ThenBy(cell => cell.x)
                .ThenBy(cell => cell.y))
            {
                var path = FindPathAStar(map, start, target);
                if (path == null || path.Count < 2)
                {
                    continue;
                }

                if (path.Count < bestPathLength)
                {
                    bestPath = path;
                    bestPathLength = path.Count;
                }
            }

            if (bestPath == null)
            {
                return ZeroAction();
            }

            var nextCell = bestPath[1];
            var nextWorld = ToCellCenterWorld(map, nextCell);
            var direction = nextWorld - agent.transform.position;
            direction.y = 0f;

            if (direction.sqrMagnitude <= 0.0001f)
            {
                return ZeroAction();
            }

            direction.Normalize();
            return new PacManAction
            {
                Acceleration = new Vector2(
                    Mathf.Clamp(direction.x, -1f, 1f),
                    Mathf.Clamp(direction.z, -1f, 1f)
                )
            };
        }

        private static List<Vector2Int> GetEnemyFoodCells(PacManAgentManager agent, ObstacleMap map)
        {
            var ownTeam = TeamAssignmentUtil.CheckTeam(agent.gameObject);
            var foods = agent.GetFoodObjects() ?? new List<GameObject>();

            return foods
                .Where(food => food != null && food.activeSelf)
                .Where(food => TeamAssignmentUtil.CheckTeam(food) != ownTeam)
                .Select(food => ToCell(map, food.transform.position))
                .Distinct()
                .ToList();
        }

        private static List<Vector2Int> FindPathAStar(ObstacleMap map, Vector2Int start, Vector2Int goal)
        {
            if (!IsTraversable(map, goal))
            {
                if (!TryFindNearestTraversableCell(map, goal, out goal))
                {
                    return null;
                }
            }

            if (start == goal)
            {
                return new List<Vector2Int> { start };
            }

            var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            var gScore = new Dictionary<Vector2Int, float> { [start] = 0f };
            var closed = new HashSet<Vector2Int>();
            var sequence = 0;

            var openSet = new SortedSet<(float f, float h, int seq, Vector2Int cell)>(
                Comparer<(float f, float h, int seq, Vector2Int cell)>.Create((a, b) =>
                {
                    var c = a.f.CompareTo(b.f);
                    if (c != 0)
                    {
                        return c;
                    }

                    c = a.h.CompareTo(b.h);
                    if (c != 0)
                    {
                        return c;
                    }

                    return a.seq.CompareTo(b.seq);
                })
            );

            var startH = ManhattanDistance(start, goal);
            openSet.Add((startH, startH, sequence++, start));

            while (openSet.Count > 0)
            {
                var current = openSet.Min;
                openSet.Remove(current);

                var currentCell = current.cell;
                if (!closed.Add(currentCell))
                {
                    continue;
                }

                if (currentCell == goal)
                {
                    return ReconstructPath(cameFrom, currentCell);
                }

                var currentG = gScore[currentCell];
                foreach (var (dx, dz) in CardinalNeighbors)
                {
                    var neighbor = new Vector2Int(currentCell.x + dx, currentCell.y + dz);
                    if (!IsTraversable(map, neighbor) || closed.Contains(neighbor))
                    {
                        continue;
                    }

                    var tentativeG = currentG + 1f;
                    if (tentativeG >= gScore.GetValueOrDefault(neighbor, float.MaxValue))
                    {
                        continue;
                    }

                    cameFrom[neighbor] = currentCell;
                    gScore[neighbor] = tentativeG;

                    var h = ManhattanDistance(neighbor, goal);
                    var f = tentativeG + h;
                    openSet.Add((f, h, sequence++, neighbor));
                }
            }

            return null;
        }

        private static List<Vector2Int> ReconstructPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int end)
        {
            var path = new List<Vector2Int> { end };
            var current = end;

            while (cameFrom.TryGetValue(current, out var previous))
            {
                current = previous;
                path.Add(current);
            }

            path.Reverse();
            return path;
        }

        private static bool TryFindNearestTraversableCell(ObstacleMap map, Vector2Int origin, out Vector2Int nearest)
        {
            nearest = default;
            var found = false;
            var bestDist = int.MaxValue;

            foreach (var kvp in map.traversabilityPerCell)
            {
                if (kvp.Value != ObstacleMap.Traversability.Free)
                {
                    continue;
                }

                var dist = ManhattanDistance(origin, kvp.Key);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = kvp.Key;
                    found = true;
                }
            }

            return found;
        }

        private static Vector2Int ToCell(ObstacleMap map, Vector3 worldPos)
        {
            var cell = map.WorldToCell(worldPos);
            return new Vector2Int(cell.x, cell.z);
        }

        private static Vector3 ToCellCenterWorld(ObstacleMap map, Vector2Int cell)
        {
            return map.CellToWorld(new Vector3Int(cell.x, 0, cell.y)) + map.trueScale / 2f;
        }

        private static bool IsTraversable(ObstacleMap map, Vector2Int cell)
        {
            return map.traversabilityPerCell.TryGetValue(cell, out var traversability)
                && traversability == ObstacleMap.Traversability.Free;
        }

        private static int ManhattanDistance(Vector2Int a, Vector2Int b)
        {
            return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
        }

        private static PacManAction ZeroAction()
        {
            return new PacManAction
            {
                Acceleration = Vector2.zero
            };
        }
    }
}