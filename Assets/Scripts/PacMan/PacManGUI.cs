using System.Collections.Generic;
using System.Linq;
using PacMan.Game;
using PacMan.Local;
using PacMan.Network;
using UnityEngine;

namespace PacMan
{
    public class PacManGUI : MonoBehaviour
    {
        private PacManManagerModeSelector _modeSelector;
        private PacManGameManager _gameManager;
        private GameStatePlaybackManager _playbackManager;

        private void Awake()
        {
            _modeSelector = FindFirstObjectByType<PacManManagerModeSelector>();
            _gameManager = _modeSelector != null ? _modeSelector.game_manager : GetComponent<PacManGameManager>();
            _playbackManager = _modeSelector != null ? _modeSelector.playback_manager : GetComponent<GameStatePlaybackManager>();
        }

        private void OnGUI()
        {
            var activeManager = ResolveActiveManager();
            if (activeManager == null)
            {
                return;
            }

            StatusLabels(activeManager);
        }

        private PacManGameManager ResolveActiveManager()
        {
            var playbackManager = _modeSelector != null ? _modeSelector.playback_manager : _playbackManager;
            if (playbackManager != null &&
                (playbackManager.ActiveMode == ManagerMode.Client || playbackManager.ActiveMode == ManagerMode.Replay))
            {
                return playbackManager;
            }

            return _modeSelector != null ? _modeSelector.game_manager : _gameManager;
        }

        private static void StatusLabels(PacManGameManager gameManager)
        {
            var style = new GUIStyle();
            style.fontSize = 20;
            style.normal.textColor = Color.white;

            var requestEntries = gameManager.GetNetworkRequestDisplayEntries()?.ToList() ?? new List<NetworkRequestDisplayEntry>();
            if (gameManager.ActiveMode == ManagerMode.Server ||
                gameManager.ActiveMode == ManagerMode.Client ||
                gameManager.ActiveMode == ManagerMode.Replay)
            {
                GUILayout.Label("Mode: " + gameManager.ActiveMode, style);
            }

            if (gameManager.ActiveMode == ManagerMode.Server)
            {
                GUILayout.Label("Port: " + CommunicatorHttpServer.channel, style);
                if (requestEntries.Count == 0)
                {
                    GUILayout.Label("Clients: waiting for connections", style);
                }
                else
                {
                    foreach (var entry in requestEntries)
                    {
                        GUILayout.Label(GetClientNameLabel(entry), style);
                    }
                }
            }
            else if (gameManager.ActiveMode == ManagerMode.Client)
            {
                GUILayout.Label("Team Name: " + gameManager.GetDisplayTeamName(), style);
            }

            var remainingTime = gameManager.matchLength <= 0f
                ? 0f
                : Mathf.Max(0f, gameManager.matchLength - gameManager.matchTime);
            GUILayout.Label("Time Remaining: " + remainingTime.ToString("0.00"), style);
            var redScore = gameManager.redScore;
            var blueScore = gameManager.blueScore;

            if (redScore > blueScore)
            {
                GUILayout.Label("Red Lead: " + (redScore - blueScore), style);
            }
            else if (blueScore > redScore)
            {
                GUILayout.Label("Blue Lead: " + (blueScore - redScore), style);
            }
            else
            {
                GUILayout.Label("Tied!", style);
            }

            GUILayout.Label("Red score: " + redScore, style);
            GUILayout.Label("Blue score: " + blueScore, style);
            GUILayout.Label("Red food: " + gameManager.redFood, style);
            GUILayout.Label("Blue food: " + gameManager.blueFood, style);
            GUILayout.Label("Red carried: " + GetCarriedCount(gameManager.redAgents), style);
            GUILayout.Label("Blue carried: " + GetCarriedCount(gameManager.blueAgents), style);

            foreach (var entry in requestEntries)
            {
                GUILayout.Label(GetRequestTimingLabel(entry), style);
            }

            GUILayout.Label("", style);
        }

        private static string GetClientNameLabel(NetworkRequestDisplayEntry entry)
        {
            return GetTeamLabel(entry) + " client: " + entry.TeamName;
        }

        private static string GetRequestTimingLabel(NetworkRequestDisplayEntry entry)
        {
            return GetTeamLabel(entry) + " request avg: " +
                   entry.AverageWaitMs.ToString("0.00") +
                   " ms (" +
                   entry.CurrentWaitMs.ToString("0.00") +
                   " ms)";
        }

        private static string GetTeamLabel(NetworkRequestDisplayEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.TeamTag))
            {
                return entry.TeamTag;
            }

            if (!string.IsNullOrWhiteSpace(entry.TeamName) && entry.TeamName != "Pending")
            {
                return entry.TeamName;
            }

            return "Team";
        }

        private static int GetCarriedCount(IEnumerable<PacManAgentManager> agents)
        {
            return agents?.Where(agent => agent != null)
                       .Select(agent => agent.foodCarried?.Count ?? 0)
                       .Sum() ?? 0;
        }
    }
}
