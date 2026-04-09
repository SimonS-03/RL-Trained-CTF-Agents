using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Network;
using PacMan.Network.Generated;
using UnityEngine;
using ProtoGameState = PacMan.Network.Generated.GameState;

namespace PacMan.Local
{
    public class GameStatePlaybackManager : PacManGameManager
    {
        public string playbackFile;

        private FileStream _replayStream;
        private Coroutine waiting;
        private CommunicatorHttpClient _clientReference;
        private bool _clientDisposedOnQuit;
        private int _controlledAgentIndex = -1;
        private Task<ProtoGameState> _pendingNetworkStateTask;
        private bool _clientInitialized;
        private float _nextInitializeRetryTime;
        private bool _loggedClientOwnershipLayout;
        private bool _loggedInitializationWaitWarning;
        private List<int> _controlledAgentIndices = new();
        private string _controlledTeamTag = string.Empty;
        private bool _configuredRecordedFixedTimeStep;
        private float _previousFixedDeltaTime;

        private const float InitializeRetryDelaySeconds = 1f;
        protected override bool ShouldDeferAgentAIInitialization => CurrentMode == ManagerMode.Client;

        public override void Start()
        {
            _loggedClientOwnershipLayout = false;
            _loggedInitializationWaitWarning = false;
            if (CurrentMode == ManagerMode.Client)
            {
                _clientReference = CommunicatorHttpClient.Instance;
                _clientReference.ResetStepRequestTiming();
                UpdateClientTransportTeamName();
                StartClientInitializeRequest();
                return;
            }

            if (CurrentMode == ManagerMode.Replay)
            {
                var replayPath = Application.streamingAssetsPath + "/Text/" + playbackFile + ".pb";
                _replayStream = File.OpenRead(replayPath);

                var replayState = ReadNextState();
                if (replayState != null)
                {
                    ConfigureRecordedFixedTimeStep(replayState);
                    waiting = StartCoroutine(WaitReplay(replayState));
                }
            }
        }

        public IEnumerator WaitReplay(ProtoGameState gameState)
        {
            yield return ApplyMapSelectionAsync(ResolveAuthoritativeMapName(gameState?.MapName));
            yield return new WaitForFixedUpdate();

            Initialize();
            ApplyAuthoritativeState(gameState, false);
            waiting = null;
        }

        private IEnumerator WaitClientInitialize(ProtoGameState gameState)
        {
            yield return ApplyMapSelectionAsync(ResolveAuthoritativeMapName(gameState?.MapName));
            yield return new WaitForFixedUpdate();

            Initialize();
            LogClientOwnershipLayout(gameState.ClientId, gameState.MapName);
            ApplyAuthoritativeState(gameState, UsesManualSimulation);
            agents.ForEach(agent => agent.InitializeAIIfNeeded());
            waiting = null;

            if (!finished)
            {
                RequestNextClientState();
            }
        }

        public new void Update()
        {
            if (CurrentMode != ManagerMode.Client)
            {
                return;
            }

            UpdateClientNetwork();
        }

        public new void FixedUpdate()
        {
            if (CurrentMode == ManagerMode.Client)
            {
                return;
            }

            if (waiting != null)
            {
                return;
            }

            if (CurrentMode == ManagerMode.Replay)
            {
                PlaybackFromFile();
            }
        }

        private void UpdateClientNetwork()
        {
            if (_pendingNetworkStateTask == null)
            {
                if (!_clientInitialized)
                {
                    if (Time.unscaledTime >= _nextInitializeRetryTime)
                    {
                        StartClientInitializeRequest();
                    }

                    return;
                }

                if (started && !finished)
                {
                    RequestNextClientState();
                }

                return;
            }

            if (!_pendingNetworkStateTask.IsCompleted)
            {
                return;
            }

            if (_pendingNetworkStateTask.IsFaulted)
            {
                Debug.LogException(_pendingNetworkStateTask.Exception);
                _pendingNetworkStateTask = null;
                return;
            }

            if (_pendingNetworkStateTask.IsCanceled)
            {
                _pendingNetworkStateTask = null;
                return;
            }

            var readState = _pendingNetworkStateTask.Result;
            _pendingNetworkStateTask = null;
            if (readState == null)
            {
                if (!_clientInitialized)
                {
                    _nextInitializeRetryTime = Time.unscaledTime + InitializeRetryDelaySeconds;
                    LogInitializationWaitWarning();
                }

                return;
            }

            if (!_clientInitialized)
            {
                UpdateControlledOwnership(readState);
                _clientInitialized = true;
                ConfigureRecordedFixedTimeStep(readState);
                waiting = StartCoroutine(WaitClientInitialize(readState));
                return;
            }

            ApplyAuthoritativeState(readState, UsesManualSimulation);

            if (!finished)
            {
                RequestNextClientState();
            }
        }

        private void RequestNextClientState()
        {
            var actions = CollectClientActions();
            _pendingNetworkStateTask = _clientReference.StepAsync(actions);
        }

        private void StartClientInitializeRequest()
        {
            _nextInitializeRetryTime = Time.unscaledTime + InitializeRetryDelaySeconds;
            _pendingNetworkStateTask = _clientReference.InitializeAsync();
        }

        private string ResolveAuthoritativeMapName(string authoritativeMapName)
        {
            if (!string.IsNullOrWhiteSpace(authoritativeMapName))
            {
                return authoritativeMapName;
            }

            if (!string.IsNullOrWhiteSpace(PacManManagerModeSelector.map_override))
            {
                return PacManManagerModeSelector.map_override;
            }

            return mapManager?.fileName;
        }

        private void LogInitializationWaitWarning()
        {
            if (_loggedInitializationWaitWarning)
            {
                return;
            }

            Debug.Log($"Client waiting for initial game state on port {CommunicatorHttpClient.channel}. Keeping the current scene map visible until the server responds.");
            _loggedInitializationWaitWarning = true;
        }

        private void PlaybackFromFile()
        {
            var readState = ReadNextState();
            if (readState == null)
            {
                return;
            }

            ApplyAuthoritativeState(readState, false);
        }

        private void ApplyAuthoritativeState(ProtoGameState readState, bool simulatePhysics)
        {
            ConfigureRecordedFixedTimeStep(readState);
            UpdateControlledOwnership(readState);
            if (_clientReference != null && readState != null && readState.MatchTime < matchTime)
            {
                _clientReference.ResetStepRequestTiming();
            }

            GameStateParser.ApplyState(this, readState);
            matchTime = readState.MatchTime;

            Physics.SyncTransforms();
            if (simulatePhysics)
            {
                Physics.Simulate(Time.fixedDeltaTime);
                Physics.SyncTransforms();
            }

            agents.ForEach(agent => agent.RefreshPresentation());
            started = true;
            RefreshDerivedState();
        }

        public bool IsControlledAgent(PacManAgentManager agent)
        {
            if (agent == null)
            {
                return false;
            }

            var agentIndex = agents?.IndexOf(agent) ?? -1;
            return agentIndex >= 0 && _controlledAgentIndices.Contains(agentIndex);
        }

        private List<PacManAction> CollectClientActions()
        {
            var actions = new List<PacManAction>();
            foreach (var agentIndex in _controlledAgentIndices)
            {
                if (agentIndex < 0 || agentIndex >= agents.Count)
                {
                    continue;
                }

                var agent = agents[agentIndex];
                var controlledAgent = agent.GetComponent<GameStatePlaybackAgentManager>();
                var action = controlledAgent?.SampleAction() ?? new PacManAction();
                action.Index = agentIndex;
                actions.Add(action);
            }

            return actions;
        }

        private bool TryGetControlledTeamTag(out string controlledTeamTag)
        {
            controlledTeamTag = _controlledTeamTag;
            return !string.IsNullOrWhiteSpace(controlledTeamTag);
        }

        private void UpdateControlledOwnership(ProtoGameState readState)
        {
            _controlledAgentIndex = readState?.ControlledAgentIndex ?? -1;
            _controlledTeamTag = string.Empty;
            _controlledAgentIndices = new List<int>();

            if (readState?.Agents == null)
            {
                return;
            }

            var authoritativeTags = new List<string>(readState.Agents.Count);
            for (var i = 0; i < readState.Agents.Count; i++)
            {
                authoritativeTags.Add(readState.Agents[i]?.Tag ?? string.Empty);
            }

            _controlledAgentIndices = TeamAssignmentUtil.GetControlledAgentIndicesForAnchorAgent(authoritativeTags, _controlledAgentIndex);
            TeamAssignmentUtil.TryGetControlledTeamTag(authoritativeTags, _controlledAgentIndex, out _controlledTeamTag);
            UpdateClientTransportTeamName();
        }

        private void LogClientOwnershipLayout(string clientId, string mapName)
        {
            if (_loggedClientOwnershipLayout)
            {
                return;
            }

            var controlledTeamTag = TryGetControlledTeamTag(out var teamTag) ? teamTag : "unassigned";
            Debug.Log($"Client {clientId} controlling team {controlledTeamTag} agents [{string.Join(",", _controlledAgentIndices)}] on map {mapName}.");
            _loggedClientOwnershipLayout = true;
        }

        public override string GetDisplayTeamName()
        {
            return ResolveDisplayTeamName(teamName, _controlledTeamTag);
        }

        public override IReadOnlyList<NetworkRequestDisplayEntry> GetNetworkRequestDisplayEntries()
        {
            if (CurrentMode != ManagerMode.Client || _clientReference == null)
            {
                return Array.Empty<NetworkRequestDisplayEntry>();
            }

            return new List<NetworkRequestDisplayEntry>
            {
                new NetworkRequestDisplayEntry(
                    _controlledTeamTag,
                    GetDisplayTeamName(),
                    _clientReference.AverageStepRequestWaitMs,
                    _clientReference.CurrentStepRequestWaitMs)
            };
        }

        private void UpdateClientTransportTeamName()
        {
            if (_clientReference == null)
            {
                return;
            }

            _clientReference.TeamName = GetOutboundTeamName();
        }

        private string GetOutboundTeamName()
        {
            if (!string.IsNullOrWhiteSpace(teamName))
            {
                return teamName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_controlledTeamTag))
            {
                return $"{_controlledTeamTag} Team";
            }

            return string.Empty;
        }

        private ProtoGameState ReadNextState()
        {
            return _replayStream == null ? null : GameStateParser.ReadState(_replayStream);
        }

        private void ConfigureRecordedFixedTimeStep(ProtoGameState gameState)
        {
            if (gameState == null || gameState.FixedDeltaTime <= 0f)
            {
                return;
            }

            if (!_configuredRecordedFixedTimeStep)
            {
                _previousFixedDeltaTime = Time.fixedDeltaTime;
                _configuredRecordedFixedTimeStep = true;
            }

            if (!Mathf.Approximately(Time.fixedDeltaTime, gameState.FixedDeltaTime))
            {
                Time.fixedDeltaTime = gameState.FixedDeltaTime;
            }
        }

        private void RestoreRecordedFixedTimeStep()
        {
            if (!_configuredRecordedFixedTimeStep)
            {
                return;
            }

            Time.fixedDeltaTime = _previousFixedDeltaTime;
            _configuredRecordedFixedTimeStep = false;
        }

        private void OnApplicationQuit()
        {
            DisposeClientOnQuit();
        }

        private void DisposeClientOnQuit()
        {
            if (_clientDisposedOnQuit)
            {
                return;
            }

            _clientDisposedOnQuit = true;
            _pendingNetworkStateTask = null;

            if (!CommunicatorHttpClient.IsInitialized)
            {
                _clientReference = null;
                return;
            }

            try
            {
                CommunicatorHttpClient.Instance.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to dispose HTTP client during application quit: {e}");
            }
            finally
            {
                _clientReference = null;
            }
        }

        private void OnDestroy()
        {
            _replayStream?.Dispose();
            _replayStream = null;
            RestoreRecordedFixedTimeStep();
            RestorePhysicsSimulationMode();
        }
    }
}
