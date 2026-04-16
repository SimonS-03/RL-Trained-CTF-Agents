using System.Collections.Generic;
using Scripts.Map;
using UnityEngine;

/// <summary>
/// Computes a grid-based flow field for a given goal using Dijkstra's algorithm
/// over an ObstacleMap. Provides cost-to-goal and direction-to-goal queries
/// from any world position.
/// </summary>
public class FlowField
{
    /// <summary>Cost from each cell to the goal. float.MaxValue = unreachable.</summary>
    public Dictionary<Vector2Int, float> CostField { get; private set; }
    private Dictionary<Vector2Int, float> wallPotentialField; 

    /// <summary>Best direction to follow from each cell toward the goal (world-space, normalized).</summary>
    public Dictionary<Vector2Int, Vector3> DirectionField { get; private set; }

    private readonly ObstacleMap obstacleMap;
    private readonly List<Vector2Int> goalCells;

    // 8-connected neighbors (dx, dz) with associated movement cost
    private static readonly (int dx, int dz, float cost)[] Neighbors =
    {
        ( 1,  0, 1f),
        (-1,  0, 1f),
        ( 0,  1, 1f),
        ( 0, -1, 1f),
        ( 1,  1, 1.41421356f),
        ( 1, -1, 1.41421356f),
        (-1,  1, 1.41421356f),
        (-1, -1, 1.41421356f),
    };

    private FlowField(ObstacleMap obstacleMap, List<Vector2Int> goalCells)
    {
        this.obstacleMap = obstacleMap;
        this.goalCells = goalCells;
        // Debug.Log("goalCells: " + goalCells.Count);
    }

    /// <summary>
    /// Build a flow field for a goal located at <paramref name="goalWorldPos"/>.
    /// Uses Dijkstra over the ObstacleMap grid (8-connected).
    /// </summary>
    public static FlowField Build(ObstacleMap obstacleMap, List<Vector3> goalsWorldPos)
    {
        // Debug.Log(goalsWorldPos.Count);
        var cells = new List<Vector2Int>();
        foreach(var pos in goalsWorldPos)
        {
            var cell3 = obstacleMap.WorldToCell(pos);
            cells.Add(new Vector2Int(cell3.x, cell3.z));
        }
        var ff = new FlowField(obstacleMap, cells);
        ff.ComputeCostField();
        ff.ComputeDirectionField();
        return ff;
    }

    // ───────── Dijkstra ─────────

    private void ComputeCostField()
    {
        CostField = new Dictionary<Vector2Int, float>();
        // wallPotentialField = new Dictionary<Vector2Int, float>();

        // Initialize all free cells to max cost
        foreach (var kvp in obstacleMap.traversabilityPerCell)
        {
            CostField[kvp.Key] = float.MaxValue;
        }

        // (We suppose here that food is always on a free cell)
        // If the goal cell itself is blocked or unmapped, find the nearest free cell
        // if (!IsTraversable(startCell))
        // {
        //     startCell = FindNearestFreeCell(startCell);
        //     if (startCell.x == int.MinValue)
        //         return; // no free cells at all
        // }

        
        // Min-heap: (cost, cell)
        var openSet = new SortedSet<(float cost, Vector2Int cell)>(
            Comparer<(float cost, Vector2Int cell)>.Create((a, b) =>
            {
                int c = a.cost.CompareTo(b.cost);
                if (c != 0) return c;
                c = a.cell.x.CompareTo(b.cell.x);
                return c != 0 ? c : a.cell.y.CompareTo(b.cell.y);
            }));

        foreach(var cell in goalCells)
        {
            CostField[cell] = 0f;
            // CostField[cell] += wallPotential(cell); // Add potential based on proximity to walls (optional)
            // wallPotentialField[cell] = wallPotential(cell);

            openSet.Add((0f, cell));
        }

        while (openSet.Count > 0)
        {
            var (currentCost, current) = openSet.Min;
            openSet.Remove(openSet.Min);

            // Skip if we already found a cheaper path
            if (currentCost > CostField[current])
                continue;

            foreach (var (dx, dz, moveCost) in Neighbors)
            {
                var neighbor = new Vector2Int(current.x + dx, current.y + dz);

                if (!IsTraversable(neighbor))
                    continue;

                // Disallow diagonal movement if both adjacent cardinal cells are blocked (corner-cutting)
                if (dx != 0 && dz != 0)
                {
                    var adj1 = new Vector2Int(current.x + dx, current.y);
                    var adj2 = new Vector2Int(current.x, current.y + dz);
                    if (!IsTraversable(adj1) || !IsTraversable(adj2))
                        continue;
                }

                float newCost = currentCost + moveCost;
                if (newCost < CostField.GetValueOrDefault(neighbor, float.MaxValue))
                {
                    CostField[neighbor] = newCost;
                    openSet.Add((newCost, neighbor));
                }
            }
        }

    }

    // ───────── Direction field ─────────

    private void ComputeDirectionField()
    {
        DirectionField = new Dictionary<Vector2Int, Vector3>();

        foreach (var kvp in CostField)
        {
            var cell = kvp.Key;
            float myCost = kvp.Value;

            if (myCost >= float.MaxValue)
            {
                DirectionField[cell] = Vector3.zero;
                continue;
            }

            // Pick the neighbor with the steepest cost descent
            float bestCost = myCost;
            Vector2Int bestNeighbor = cell;

            foreach (var (dx, dz, _) in Neighbors)
            {
                var n = new Vector2Int(cell.x + dx, cell.y + dz);
                float nCost = CostField.GetValueOrDefault(n, float.MaxValue);
                if (nCost < bestCost)
                {
                    if(dx != 0 && dz != 0)
                    {
                        var adj1 = new Vector2Int(cell.x + dx, cell.y);
                        var adj2 = new Vector2Int(cell.x, cell.y + dz);
                        if (!IsTraversable(adj1) || !IsTraversable(adj2))
                            continue; // skip diagonal if corner-cutting
                    }
                    bestCost = nCost;
                    bestNeighbor = n;
                }
            }

            if (bestNeighbor == cell)
            {
                // We are between two goals or at the goal
                DirectionField[cell] = Vector3.zero;
            }
            else
            {
                // Direction in world space from current cell center to best neighbor cell center
                Vector3 from = obstacleMap.CellToWorld(new Vector3Int(cell.x, 0, cell.y));
                Vector3 to = obstacleMap.CellToWorld(new Vector3Int(bestNeighbor.x, 0, bestNeighbor.y));
                Vector3 dir = (to - from).normalized;
                dir.y = 0f; // keep planar
                DirectionField[cell] = dir;
            }
        }

        // smoothAverage(3);
    }

    // ───────── Public query API ─────────

    /// <summary>
    /// Returns the flow direction at a world position (normalized, world-space).
    /// Returns Vector3.zero if the position is unreachable or outside the grid.
    /// </summary>
    public Vector3 GetDirection(Vector3 worldPos)
    {
        var cell3 = obstacleMap.WorldToCell(worldPos);
        var cell = new Vector2Int(cell3.x, cell3.z);
        return DirectionField.GetValueOrDefault(cell, Vector3.zero);
    }

    /// <summary>
    /// Returns the cost-to-goal from a world position.
    /// Returns float.MaxValue if unreachable or outside the grid.
    /// </summary>
    public float GetCost(Vector3 worldPos)
    {
        var cell3 = obstacleMap.WorldToCell(worldPos);
        var cell = new Vector2Int(cell3.x, cell3.z);
        return CostField.GetValueOrDefault(cell, float.MaxValue);
    }

    // ───────── Helpers ─────────

    private bool IsTraversable(Vector2Int cell)
    {
        return obstacleMap.traversabilityPerCell.TryGetValue(cell, out var t)
               && t == ObstacleMap.Traversability.Free;
    }

    private void smoothAverage(int iterations)
    {
        for (int it = 0; it < iterations; it++)
        {
            foreach (var kvp in CostField)
            {
                if(kvp.Value >= float.MaxValue)
                    continue; // skip unreachable cells
                var cell = kvp.Key;
                var cellDir = DirectionField[cell];
                var dir = Vector3.zero;
                foreach (var (dx, dz, _) in Neighbors)
                {
                    var n = new Vector2Int(cell.x + dx, cell.y + dz);
                    if (DirectionField.TryGetValue(n, out var nDir))
                    {
                        dir += nDir;
                    }
                }
                dir = dir + 2f * cellDir; // Add the original direction to the sum of neighbors' directions
                DirectionField[cell] = dir.normalized;
            }
        }
    }
}
