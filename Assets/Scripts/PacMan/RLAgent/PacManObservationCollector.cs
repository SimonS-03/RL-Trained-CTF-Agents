using System;
using System.Collections.Generic;
using System.Linq;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Scripts.VecEnv.Message;
using UnityEngine;

namespace PacMan.RLAgent
{
    /// <summary>
    /// Collects only the shared core observations used by every PacMan RL role.
    /// Role-specific signals are appended by each concrete agent class.
    /// </summary>
    public static class PacManObservationCollector
    {
        // Observation dimension constants
        public const int EnemyObservations = 4;
        public const int FriendlyObservations = 3;
        public const int RaycastObsDim = 8;  // 8 obstacle rays
        public const int EnemyObsDim = 9;
        public const int FriendlyObsDim = 8;

        private static readonly (int dx, int dz)[] rayDirections =
        {
            (-1, -1),
            (-1, 0),
            (-1, 1),
            (0, -1),
            (0, 1),
            (1, -1),
            (1, 0),
            (1, 1)
        };

        /// <summary>
        /// Collect shared observations for a PacMan agent.
        ///
        /// Shared layout:
        /// - Self velocity [2]
        /// - Raycasts [8]
        /// - Distance to nearest enemy [1]
        /// - Enemies [4 x 9]
        /// - Friendlies [3 x 8]
        /// </summary>
        public static AgentObservation CollectObservation(
            AgentObservation observation, 
            PacManAgentManager thisAgent, 
            AgentCoordinator coordinator)
        {  
            observation.AppendContinuous(Mathf.Clamp01(thisAgent.GetVelocity().x / 5f));
            observation.AppendContinuous(Mathf.Clamp01(thisAgent.GetVelocity().z / 5f));

            // Raycasts for obstacle detection (8 rays)
            foreach(var (dx, dz) in rayDirections)
            {
                var direction = dx * Vector3.right + dz * Vector3.forward;
                bool intersects = Physics.Raycast(thisAgent.transform.position, direction, out RaycastHit hitInfo, 10f, LayerMask.GetMask("Obstacle"));
                if(intersects)
                    observation.AppendContinuous(Mathf.Clamp01(hitInfo.distance / 10f));
                else
                    observation.AppendContinuous(1f);
            }

            // Enemy observations
            var visibleEnemyAgents = coordinator != null
                ? coordinator.GetSharedVisibleEnemies()
                : thisAgent.GetVisibleEnemyAgents();
            var orderedVisibleEnemies = visibleEnemyAgents
                .OrderBy(enemy => Vector3.Distance(enemy.transform.position, thisAgent.transform.position))
                .ToList();
            var approximatedEnemies = GetApproximateEnemyObservations(thisAgent, visibleEnemyAgents);
            
            if(orderedVisibleEnemies.Count > 0)
                observation.AppendContinuous(Mathf.Clamp01(Vector3.Distance(thisAgent.transform.position, orderedVisibleEnemies[0].transform.position) / 30f));
            else
                observation.AppendContinuous(1f);

            int enemyIndex = 0;
            while(enemyIndex < Mathf.Min(orderedVisibleEnemies.Count, EnemyObservations))
            {
                PacManAgentManager enemy = orderedVisibleEnemies[enemyIndex];
                var relPos = enemy.transform.position - thisAgent.transform.position;
                var relVel = enemy.GetVelocity() - thisAgent.GetVelocity();
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                observation.AppendContinuous(enemy.IsGhost() ? 1f : 0f);
                observation.AppendContinuous(enemy.IsScared() ? 1f : 0f);
                observation.AppendContinuous(Mathf.Clamp01(enemy.GetScaredRemainingDuration() / 20f));
                observation.AppendContinuous(Mathf.Clamp01(enemy.GetCarriedFoodCount() / 10f));
                observation.AppendContinuous(1f); // Visible enemy flag
                enemyIndex++;
            }

            int approximatedEnemyIndex = 0;
            while (enemyIndex < EnemyObservations && approximatedEnemyIndex < approximatedEnemies.Count)
            {
                var enemyObservation = approximatedEnemies[approximatedEnemyIndex];
                var estimatedWorldPos = ToWorldPosition(thisAgent, enemyObservation.Position);
                var relPos = estimatedWorldPos - thisAgent.transform.position;

                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(0f);
                observation.AppendContinuous(0f);

                observation.AppendContinuous(enemyObservation.IsGhost ? 1f : 0f);
                observation.AppendContinuous(0f);
                observation.AppendContinuous(0f);
                observation.AppendContinuous(enemyObservation.HasFood ? 1f : 0f);
                observation.AppendContinuous(0f); // Not visible flag for approximated enemy

                enemyIndex++;
                approximatedEnemyIndex++;
            }

            while(enemyIndex < EnemyObservations)
            {
                for (int j = 0; j < EnemyObsDim; j++)
                {
                    observation.AppendContinuous(0f);
                }
                enemyIndex++;
            }

            // Friendly agent observations
            var friendlyAgents = thisAgent.GetFriendlyAgents() ?? new List<PacManAgentManager>();
            var orderedFriendlies = friendlyAgents
                .OrderBy(friend => Vector3.Distance(friend.transform.position, thisAgent.transform.position))
                .ToList();

            int friendlyIndex = 0;
            while (friendlyIndex < Mathf.Min(orderedFriendlies.Count, FriendlyObservations))
            {
                var friendly = orderedFriendlies[friendlyIndex];
                var relPos = friendly.transform.position - thisAgent.transform.position;
                var relVel = friendly.GetVelocity() - thisAgent.GetVelocity();
                observation.AppendContinuous(Mathf.Clamp(relPos.x / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relPos.z / 30f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.x / 5f, -1f, 1f));
                observation.AppendContinuous(Mathf.Clamp(relVel.z / 5f, -1f, 1f));

                observation.AppendContinuous(friendly.IsGhost() ? 1f : 0f);
                observation.AppendContinuous(friendly.IsScared() ? 1f : 0f);
                observation.AppendContinuous(Mathf.Clamp01(friendly.GetScaredRemainingDuration() / 20f));
                observation.AppendContinuous(Mathf.Clamp01(friendly.GetCarriedFoodCount() / 10f));
                friendlyIndex++;
            }
            while (friendlyIndex < FriendlyObservations)
            {
                for (int j = 0; j < FriendlyObsDim; j++)
                {
                    observation.AppendContinuous(0f);
                }
                friendlyIndex++;
            }

            return observation;
        }

        /// <summary>
        /// Get the distance from the agent to its home territory border.
        /// </summary>
        public static float GetDistanceToHomeBorder(PacManAgentManager agent)
        {
            if (agent == null)
            {
                return 0f;
            }

            var homeBorderX = TeamAssignmentUtil.CheckTeam(agent.gameObject) == Team.Blue ? -0.3f : 0.3f;
            return Mathf.Abs(agent.transform.localPosition.x - homeBorderX);
        }

        /// <summary>
        /// Check if agent is currently in its home territory.
        /// </summary>
        public static bool IsInHomeTerritory(PacManAgentManager agent)
        {
            if (agent == null)
            {
                return false;
            }

            var x = agent.transform.localPosition.x;
            if (TeamAssignmentUtil.CheckTeam(agent.gameObject) == Team.Blue)
            {
                return x >= -0.3f;
            }
            return x <= 0.3f;
        }

        private static Vector3 ToWorldPosition(PacManAgentManager referenceAgent, Vector3 localPosition)
        {
            var parent = referenceAgent != null ? referenceAgent.transform.parent : null;
            return parent != null ? parent.TransformPoint(localPosition) : localPosition;
        }

        private static List<PacManObservation> GetApproximateEnemyObservations(
            PacManAgentManager selfAgent,
            IEnumerable<PacManAgentManager> visibleEnemies)
        {
            var visibleEnemyIds = new HashSet<int>();
            if (visibleEnemies != null)
            {
                foreach (var visibleEnemy in visibleEnemies)
                {
                    if (visibleEnemy != null && visibleEnemy.serverIndex >= 0)
                    {
                        visibleEnemyIds.Add(visibleEnemy.serverIndex);
                    }
                }
            }

            var enemyObservations = selfAgent.GetEnemyObservations().Observations ?? Array.Empty<PacManObservation>();
            var approximatedEnemies = new List<PacManObservation>();

            foreach (var enemyObservation in enemyObservations)
            {
                if (enemyObservation.Visible)
                {
                    continue;
                }

                if (enemyObservation.ServerIndex >= 0 && visibleEnemyIds.Contains(enemyObservation.ServerIndex))
                {
                    continue;
                }

                // Zeroed positions are emitted when uncertainty is too high.
                if (enemyObservation.Position.sqrMagnitude < 1e-6f)
                {
                    continue;
                }

                approximatedEnemies.Add(enemyObservation);
            }

            approximatedEnemies.Sort((a, b) =>
            {
                var aDistSq = (a.Position - selfAgent.transform.localPosition).sqrMagnitude;
                var bDistSq = (b.Position - selfAgent.transform.localPosition).sqrMagnitude;
                return aDistSq.CompareTo(bDistSq);
            });

            return approximatedEnemies;
        }
    }
}
