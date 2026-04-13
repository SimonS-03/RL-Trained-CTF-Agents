using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using PacMan.Network.Generated;
using UnityEngine;
using ProtoEdible = PacMan.Network.Generated.ProtoEdible;
using ProtoGameState = PacMan.Network.Generated.GameState;
using ProtoPacManObservation = PacMan.Network.Generated.ProtoPacManObservation;
using ProtoPacManObservations = PacMan.Network.Generated.ProtoPacManObservations;
using ProtoPacManState = PacMan.Network.Generated.ProtoPacManState;
using ProtoQuaternion = PacMan.Network.Generated.ProtoQuaternion;
using ProtoTransform = PacMan.Network.Generated.ProtoTransform;
using ProtoVector2 = PacMan.Network.Generated.ProtoVector2;
using ProtoVector3 = PacMan.Network.Generated.ProtoVector3;

namespace PacMan
{
    public static class GameStateParser
    {
        public static ProtoGameState WriteState(PacManGameManager gameManager)
        {
            var storeState = CreateBaseState(gameManager);
            storeState.Agents.Add(gameManager.agents
                .Select(agent => agent.GetComponent<PacManAgentManager>())
                .Select(PacManStateFromObject));

            return storeState;
        }

        public static ProtoGameState WriteStateForClient(PacManGameManager gameManager, int clientSlot, int expectedClientCount = TeamAssignmentUtil.ExpectedNetworkClientCount)
        {
            if (gameManager == null)
            {
                throw new ArgumentNullException(nameof(gameManager));
            }

            var agents = gameManager.agents ?? throw new ArgumentNullException(nameof(gameManager.agents));
            var agentTags = agents.Select(agent => agent != null ? agent.tag : string.Empty).ToList();
            var controlledAgentIndices = TeamAssignmentUtil.GetControlledAgentIndicesForClientSlot(agentTags, clientSlot, expectedClientCount);
            if (controlledAgentIndices.Count == 0)
            {
                return WriteState(gameManager);
            }

            var controlledAgentSet = controlledAgentIndices.ToHashSet();
            var controlledTeamTag = agentTags[controlledAgentIndices[0]];
            var enemyAgentIndices = GetEnemyAgentIndices(agentTags, controlledTeamTag);
            var visibleEnemyIndices = GetVisibleEnemyIndicesForClient(gameManager, controlledAgentIndices, enemyAgentIndices);

            var storeState = CreateBaseState(gameManager);
            for (var agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var agent = agents[agentIndex].GetComponent<PacManAgentManager>();
                if (controlledAgentSet.Contains(agentIndex))
                {
                    storeState.Agents.Add(PacManStateFromObject(agent));
                    continue;
                }

                if (visibleEnemyIndices.Contains(agentIndex))
                {
                    storeState.Agents.Add(PacManStateFromObject(agent, null, false));
                    continue;
                }

                storeState.Agents.Add(HiddenPacManStateFromObject(agent));
            }

            return storeState;
        }

        public static ProtoGameState ReadState(byte[] gameState)
        {
            if (gameState == null || gameState.Length == 0)
            {
                return null;
            }

            return ProtoGameState.Parser.ParseFrom(gameState);
        }

        public static ProtoGameState ReadState(Stream stream)
        {
            if (stream == null || !stream.CanRead)
            {
                return null;
            }

            if (stream.CanSeek && stream.Position >= stream.Length)
            {
                return null;
            }

            return ProtoGameState.Parser.ParseDelimitedFrom(stream);
        }

        public static void WriteState(Stream stream, ProtoGameState gameState)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (gameState == null)
            {
                throw new ArgumentNullException(nameof(gameState));
            }

            gameState.WriteDelimitedTo(stream);
        }

        public static ProtoGameState WithClientContext(ProtoGameState gameState, string clientId, int controlledAgentIndex)
        {
            var cloned = gameState?.Clone() ?? new ProtoGameState();
            cloned.ClientId = clientId ?? string.Empty;
            cloned.ControlledAgentIndex = controlledAgentIndex;
            return cloned;
        }

        public static void ApplyState(PacManGameManager gameManager, ProtoGameState readState)
        {
            if (gameManager == null)
            {
                throw new ArgumentNullException(nameof(gameManager));
            }

            if (readState == null)
            {
                throw new ArgumentNullException(nameof(readState));
            }

            ApplyAuthoritativeSimulationState(gameManager, readState);

            var agentCount = Math.Min(readState.Agents.Count, gameManager.agents.Count);
            for (int i = 0; i < agentCount; i++)
            {
                ApplyAgentState(gameManager.agents[i].GetComponent<PacManAgentManager>(), readState.Agents[i], i);
            }

            var foodCount = Math.Min(readState.Food.Count, gameManager.foodList.Count);
            for (int i = 0; i < foodCount; i++)
            {
                ApplyEdibleState(gameManager.foodList[i], readState.Food[i]);
            }

            var capsuleCount = Math.Min(readState.Capsules.Count, gameManager.capsules.Count);
            for (int i = 0; i < capsuleCount; i++)
            {
                ApplyEdibleState(gameManager.capsules[i], readState.Capsules[i]);
            }
        }

        private static ProtoPacManState PacManStateFromObject(PacManAgentManager agent)
        {
            return PacManStateFromObject(agent, null);
        }

        private static ProtoPacManState PacManStateFromObject(PacManAgentManager agent, PacManObservations? observationsOverride, bool includeObservations = true)
        {
            var rigidBody = agent.GetComponent<Rigidbody>();
            var state = new ProtoPacManState
            {
                Transform = TransformFromTransform(agent.transform),
                FoodCarried = agent.foodCarried.Count,
                IsScared = agent.isScared,
                IsGhost = agent.isGhost,
                IsPowered = agent.IsPoweredUp(),
                ScaredRemaining = agent.GetScaredRemainingDuration(),
                Direction = Vector2FromUnity(agent.action.Acceleration),
                Magnitude = agent.action.Acceleration.magnitude,
                Velocity = Vector3FromUnity(rigidBody.linearVelocity),
                AngularVelocity = Vector3FromUnity(rigidBody.angularVelocity),
                Tag = agent.tag ?? string.Empty,
                ServerIndex = agent.serverIndex,
                LastRespawnStep = agent.GetLastRespawnStep()
            };

            if (includeObservations)
            {
                state.Observations = ObservationsFromValue(observationsOverride ?? agent.GetEnemyObservations());
            }

            return state;
        }

        private static ProtoPacManState HiddenPacManStateFromObject(PacManAgentManager agent)
        {
            return new ProtoPacManState
            {
                Tag = agent.tag ?? string.Empty,
                ServerIndex = agent.serverIndex,
                LastRespawnStep = agent.GetLastRespawnStep()
            };
        }

        private static void ApplyAgentState(PacManAgentManager agent, ProtoPacManState state, int fallbackServerIndex)
        {
            agent.tag = state?.Tag ?? agent.tag;
            agent.serverIndex = ResolveServerIndex(state, fallbackServerIndex);
            agent.SetLastRespawnStep(state?.LastRespawnStep ?? 0);

            var hasAuthoritativeState = state?.Transform != null;
            agent.gameObject.SetActive(hasAuthoritativeState);
            if (!hasAuthoritativeState)
            {
                agent.foodCarried.Clear();
                agent.SetEnemyObservations(ToLocalObservations(state?.Observations));

                var hiddenRigidBody = agent.GetComponent<Rigidbody>();
                hiddenRigidBody.linearVelocity = Vector3.zero;
                hiddenRigidBody.angularVelocity = Vector3.zero;
                return;
            }

            if (state.Transform != null)
            {
                agent.transform.localPosition = ToUnityVector3(state.Transform.Position);
                agent.transform.localRotation = ToUnityQuaternion(state.Transform.Rotation);
            }

            agent.foodCarried.Clear();
            for (int i = 0; i < state.FoodCarried; i++)
            {
                agent.foodCarried.Add(null);
            }

            agent.isScared = state.IsScared;
            agent.isGhost = state.IsGhost;
            agent.scaredUntil = agent.GetSimulationTime() + state.ScaredRemaining;
            agent.action = new PacManAction
            {
                Acceleration = ToUnityVector2(state.Direction)
            };
            agent.SetEnemyObservations(ToLocalObservations(state.Observations));

            var rigidBody = agent.GetComponent<Rigidbody>();
            rigidBody.linearVelocity = ToUnityVector3(state.Velocity);
            rigidBody.angularVelocity = ToUnityVector3(state.AngularVelocity);
        }

        private static ProtoGameState CreateBaseState(PacManGameManager gameManager)
        {
            var mapName = gameManager.mapManager?.fileName;

            var storeState = new ProtoGameState
            {
                MatchTime = gameManager.matchTime,
                MapName = mapName ?? string.Empty,
                FixedDeltaTime = Time.fixedDeltaTime,
                StepsSinceMatchStart = gameManager.CurrentSimulationStep,
                StepsRemaining = gameManager.RemainingSimulationSteps,
                MatchLength = gameManager.matchLength
            };

            storeState.Food.Add(gameManager.foodList.Select(food => EdibleFromObject(food)));
            storeState.Capsules.Add(gameManager.capsules.Select(capsule => EdibleFromObject(capsule, true)));
            return storeState;
        }

        private static ProtoEdible EdibleFromObject(GameObject gameObject, bool isCapsule = false)
        {
            return new ProtoEdible
            {
                IsCapsule = isCapsule,
                IsActive = gameObject.activeSelf,
                Transform = TransformFromTransform(gameObject.transform)
            };
        }

        private static void ApplyEdibleState(GameObject gameObject, ProtoEdible edible)
        {
            if (edible.Transform != null)
            {
                gameObject.transform.localPosition = ToUnityVector3(edible.Transform.Position);
                gameObject.transform.localRotation = ToUnityQuaternion(edible.Transform.Rotation);
            }

            gameObject.SetActive(edible.IsActive);
        }

        private static ProtoPacManObservations ObservationsFromValue(PacManObservations observations)
        {
            var protoObservations = new ProtoPacManObservations
            {
                Index = observations.Index,
                ObservationFixedTime = observations.ObservationFixedTime,
                AgentServerIndex = observations.AgentServerIndex
            };

            protoObservations.Observations.Add((observations.Observations ?? Array.Empty<PacManObservation>())
                .Select(ObservationFromValue));
            return protoObservations;
        }

        private static List<int> GetEnemyAgentIndices(IReadOnlyList<string> agentTags, string controlledTeamTag)
        {
            return agentTags
                .Select((tag, index) => (tag, index))
                .Where(pair => !string.Equals(pair.tag, controlledTeamTag, StringComparison.Ordinal))
                .Select(pair => pair.index)
                .ToList();
        }

        private static HashSet<int> GetVisibleEnemyIndicesForClient(PacManGameManager gameManager, IReadOnlyList<int> controlledAgentIndices, IReadOnlyList<int> enemyAgentIndices)
        {
            var visibleEnemyIndices = new HashSet<int>();
            foreach (var controlledAgentIndex in controlledAgentIndices)
            {
                if (controlledAgentIndex < 0 || controlledAgentIndex >= gameManager.agents.Count)
                {
                    continue;
                }

                var observations = gameManager.agents[controlledAgentIndex].GetEnemyObservations().Observations ?? Array.Empty<PacManObservation>();
                var observationCount = Math.Min(observations.Length, enemyAgentIndices.Count);
                for (var observationIndex = 0; observationIndex < observationCount; observationIndex++)
                {
                    if (observations[observationIndex].Visible)
                    {
                        var observationServerIndex = observations[observationIndex].ServerIndex;
                        visibleEnemyIndices.Add(observationServerIndex >= 0 ? observationServerIndex : enemyAgentIndices[observationIndex]);
                    }
                }
            }

            return visibleEnemyIndices;
        }

        private static ProtoPacManObservation ObservationFromValue(PacManObservation observation)
        {
            return new ProtoPacManObservation
            {
                Position = Vector3FromUnity(observation.Position),
                IsGhost = observation.IsGhost,
                Visible = observation.Visible,
                ReadingDispersion = observation.ReadingDispersion,
                HasFood = observation.HasFood,
                ServerIndex = observation.ServerIndex,
                LastRespawnStep = observation.LastRespawnStep
            };
        }

        private static PacManObservations ToLocalObservations(ProtoPacManObservations protoObservations)
        {
            return new PacManObservations
            {
                Index = protoObservations?.Index ?? 0,
                ObservationFixedTime = protoObservations?.ObservationFixedTime ?? 0f,
                AgentServerIndex = protoObservations?.AgentServerIndex ?? -1,
                Observations = protoObservations?.Observations.Select(ToLocalObservation).ToArray() ?? Array.Empty<PacManObservation>()
            };
        }

        private static PacManObservation ToLocalObservation(ProtoPacManObservation protoObservation)
        {
            if (protoObservation == null)
            {
                return new PacManObservation
                {
                    ServerIndex = -1,
                    LastRespawnStep = 0
                };
            }

            return new PacManObservation
            {
                Position = ToUnityVector3(protoObservation.Position),
                IsGhost = protoObservation.IsGhost,
                Visible = protoObservation.Visible,
                ReadingDispersion = protoObservation.ReadingDispersion,
                HasFood = protoObservation.HasFood,
                ServerIndex = protoObservation.ServerIndex,
                LastRespawnStep = protoObservation.LastRespawnStep
            };
        }

        private static void ApplyAuthoritativeSimulationState(PacManGameManager gameManager, ProtoGameState readState)
        {
            var fixedDeltaTime = readState.FixedDeltaTime > 0f ? readState.FixedDeltaTime : Time.fixedDeltaTime;
            var authoritativeMatchTime = readState.MatchTime;
            var authoritativeMatchLength = readState.HasMatchLength ? readState.MatchLength : gameManager.matchLength;

            var stepsSinceMatchStart = readState.StepsSinceMatchStart;
            if (stepsSinceMatchStart == 0 && authoritativeMatchTime > 0f)
            {
                stepsSinceMatchStart = gameManager.EstimateStepsSinceMatchStart(authoritativeMatchTime, fixedDeltaTime);
            }

            var stepsRemaining = readState.StepsRemaining;
            if (stepsRemaining == 0 &&
                authoritativeMatchLength > 0f &&
                authoritativeMatchTime <= authoritativeMatchLength)
            {
                stepsRemaining = gameManager.CalculateStepsRemaining(stepsSinceMatchStart, fixedDeltaTime, authoritativeMatchLength);
            }

            gameManager.ApplyAuthoritativeSimulationState(authoritativeMatchTime, stepsSinceMatchStart, stepsRemaining, authoritativeMatchLength);
        }

        private static int ResolveServerIndex(ProtoPacManState state, int fallbackServerIndex)
        {
            if (state == null)
            {
                return fallbackServerIndex;
            }

            return state.ServerIndex != 0 || fallbackServerIndex == 0
                ? state.ServerIndex
                : fallbackServerIndex;
        }

        private static ProtoTransform TransformFromTransform(Transform transform)
        {
            return new ProtoTransform
            {
                Position = Vector3FromUnity(transform.localPosition),
                Rotation = QuaternionFromUnity(transform.localRotation)
            };
        }

        private static ProtoVector2 Vector2FromUnity(Vector2 vector)
        {
            return new ProtoVector2
            {
                X = vector.x,
                Y = vector.y
            };
        }

        private static Vector2 ToUnityVector2(ProtoVector2 vector)
        {
            if (vector == null)
            {
                return Vector2.zero;
            }

            return new Vector2(vector.X, vector.Y);
        }

        private static ProtoVector3 Vector3FromUnity(Vector3 vector)
        {
            return new ProtoVector3
            {
                X = vector.x,
                Y = vector.y,
                Z = vector.z
            };
        }

        private static Vector3 ToUnityVector3(ProtoVector3 vector)
        {
            if (vector == null)
            {
                return Vector3.zero;
            }

            return new Vector3(vector.X, vector.Y, vector.Z);
        }

        private static ProtoQuaternion QuaternionFromUnity(Quaternion quaternion)
        {
            return new ProtoQuaternion
            {
                X = quaternion.x,
                Y = quaternion.y,
                Z = quaternion.z,
                W = quaternion.w
            };
        }

        private static Quaternion ToUnityQuaternion(ProtoQuaternion quaternion)
        {
            if (quaternion == null)
            {
                return Quaternion.identity;
            }

            return new Quaternion(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        }
    }
}

namespace PacMan.Network.Generated
{
    public sealed partial class GameState
    {
        public float MatchTime
        {
            get => Time;
            set => Time = value;
        }
    }
}
