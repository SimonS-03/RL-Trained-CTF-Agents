using System.Collections.Generic;
using System.Linq;
using PacMan;
using PacMan.Game;
using UnityEngine;
using Random = UnityEngine.Random;
namespace PacMan.RLAgent
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(0)]
    public class AttackFleeTrainer : MonoBehaviour
    {
        [SerializeField]
        private PacManGameManager gameManager;

        [SerializeField]
        private AgentCoordinator blueCoordinator;

        [SerializeField]
        private AgentCoordinator redCoordinator;

        [SerializeField]
        private GameObject attackerAgentPrefab;

        [SerializeField]
        private GameObject defenderAgentPrefab;

        [SerializeField]
        private bool randomizeOnAwake = true;

        private bool _configured;
        private bool _restartGameAfterConfigure;

        public Team AttackTeam { get; private set; } = Team.Undefined;
        public Team DefenseTeam { get; private set; } = Team.Undefined;
        public bool spawnOnBlueSide { get; private set; }

        private void Awake()
        {
            if (randomizeOnAwake)
            {
                TryConfigure();
            }
        }

        private void Start()
        {
            if (!_configured)
            {
                TryConfigure();
            }

            if (_restartGameAfterConfigure && gameManager != null)
            {
                gameManager.RestartGame();
                RefreshGameManagerTeamLists();
                _restartGameAfterConfigure = false;
            }
        }

        private void TryConfigure()
        {
            if (_configured)
            {
                return;
            }

            ResolveReferences();
            if (gameManager == null || blueCoordinator == null || redCoordinator == null)
            {
                Debug.LogError("AttackFleeTrainer could not find the game manager or both coordinators.");
                return;
            }

            AttackTeam = Random.value < 0.5f ? Team.Blue : Team.Red;
            DefenseTeam = AttackTeam == Team.Blue ? Team.Red : Team.Blue;
            spawnOnBlueSide = Random.value < 0.5f;

            if (attackerAgentPrefab != null || defenderAgentPrefab != null)
            {
                var bluePrefab = AttackTeam == Team.Blue ? attackerAgentPrefab : defenderAgentPrefab;
                var redPrefab = AttackTeam == Team.Red ? attackerAgentPrefab : defenderAgentPrefab;
                if (bluePrefab != null)
                {
                    gameManager.blueAgentPrefab = bluePrefab;
                }

                if (redPrefab != null)
                {
                    gameManager.redAgentPrefab = redPrefab;
                }
            }

            blueCoordinator.ConfigureTraining(true, AttackTeam == Team.Blue, true, spawnOnBlueSide);
            redCoordinator.ConfigureTraining(false, AttackTeam == Team.Red, true, spawnOnBlueSide);

            RefreshGameManagerTeamLists();
            _restartGameAfterConfigure = gameManager.started;
            _configured = true;
        }

        private void ResolveReferences()
        {
            if (gameManager == null)
            {
                gameManager = GetComponentInParent<PacManGameManager>();
            }

            if (blueCoordinator != null && redCoordinator != null)
            {
                return;
            }

            var coordinators = gameManager != null
                ? gameManager.GetComponentsInChildren<AgentCoordinator>(true).Where(coordinator => coordinator != null).ToList()
                : new List<AgentCoordinator>();

            if (coordinators.Count == 0)
            {
                Debug.LogError("AttackFleeTrainer could not find any AgentCoordinators in the game manager's children.");
                return;
            }

            if (coordinators.Count == 0)
            {
                return;
            }

            if (blueCoordinator == null)
            {
                blueCoordinator = coordinators.FirstOrDefault(coordinator => coordinator.isBlueTeam) ?? coordinators[0];
            }

            if (redCoordinator == null)
            {
                redCoordinator = coordinators.FirstOrDefault(coordinator => coordinator != blueCoordinator && !coordinator.isBlueTeam)
                    ?? coordinators.FirstOrDefault(coordinator => coordinator != blueCoordinator);
            }
        }

        private void RefreshGameManagerTeamLists()
        {
            if (gameManager == null || gameManager.agents == null)
            {
                return;
            }

            gameManager.blueAgents = gameManager.agents
                .Where(agent => agent != null && agent.CompareTag("Blue"))
                .ToList();
            gameManager.redAgents = gameManager.agents
                .Where(agent => agent != null && agent.CompareTag("Red"))
                .ToList();
        }
    }
}