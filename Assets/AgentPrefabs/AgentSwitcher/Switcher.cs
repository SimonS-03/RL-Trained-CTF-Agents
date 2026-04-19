using Unity.InferenceEngine;
using UnityEngine;
using Unity.MLAgents.Policies;
using Unity.MLAgents;
using PacMan.Agent;
using PacMan.Interface.PacMan;
using PacMan.RLAgent;
using Unity.AppUI.MVVM;
using PacMan.Local;
using UnityEngine.Windows.Speech;
using System.Collections.Generic;

public class Switcher : MonoBehaviour
{
    private enum Mode
    {
        Chase,
        Dodge,
        Collect,
        Retreat,
        Capsule
    }

    [SerializeField] private Mode currentMode = Mode.Chase;

	[Header("Dodging")]
	[SerializeField] private ModelAsset dodgerModel;
	[SerializeField] private string dodgerBehaviorName = "dodger";
    [Header("Chasing")]
    [SerializeField] private ModelAsset chaserModel;
    [SerializeField] private string chaserBehaviorName = "chaser";

	private BehaviorParameters behaviorParameters;
	private ModelAsset originalModel;
	private string originalBehaviorName;
    private PacManAI _pacManAI;
    private PacManAgentManager _agent;
    private AgentCoordinator _agentCoordinator;
    private PacManAction _inferenceAction;

    private int foodRetreatThreshold = 2;
    private const float chaseDistanceThreshold = 10f;

	private void Awake()
	{
		behaviorParameters = GetComponent<BehaviorParameters>();
        _agent = GetComponent<PacManAgentManager>();

		if (behaviorParameters == null)
		{
			Debug.LogError($"{nameof(Switcher)} requires a {nameof(BehaviorParameters)} on {name}.", this);
			enabled = false;
			return;
		}

		originalModel = behaviorParameters.Model;
		originalBehaviorName = behaviorParameters.BehaviorName;
	}

    public void Initialize()
    {
        _pacManAI = GetComponent<PacManAI>();
        if (gameObject.CompareTag("Blue"))
            _agentCoordinator = AgentCoordinator.BlueInstance;
        else
            _agentCoordinator = AgentCoordinator.RedInstance;
    }

    public void ReceiveActionFromAgent(PacManAction action)
    {
        if(_pacManAI == null)
        {
            _pacManAI = GetComponent<PacManAI>();
            return;
        }
        _inferenceAction = action;
    }

	private void Update()
	{
        ReportObservationsToCoordinator();
        ApplyAction();
        chooseMode();
	}

    private void ReportObservationsToCoordinator()
    {
        if (_agentCoordinator == null || _agent == null)
        {
            _agentCoordinator = gameObject.CompareTag("Blue") ? AgentCoordinator.BlueInstance : AgentCoordinator.RedInstance;
            return;
        }

        _agentCoordinator.ReportEnemyObservations(_agent, _agent.GetEnemyObservations());
    }

    private void ApplyAction()
    {
        switch (currentMode)
        {
            case Mode.Chase:
            case Mode.Dodge:
                _pacManAI.setAction(_inferenceAction);
                break;
            case Mode.Collect:
                var collectAction = GetDeterministicCollectAction();
                _pacManAI.setAction(collectAction);
                break;
            case Mode.Retreat:
                var retreatAction = GetDeterministicBorderAction();
                _pacManAI.setAction(retreatAction);
                break;
            case Mode.Capsule:
                var capsuleAction = getDeterministicCapsuleAction();
                _pacManAI.setAction(capsuleAction);
                break;
            default:
                break;
        }
    }

	private void ApplySwitch(Mode mode)
	{
        if(currentMode == mode)
            return;
        Debug.Log($"Switching to {mode} mode.");
        currentMode = mode;
        switch (mode)
        {
            case Mode.Chase:
                behaviorParameters.Model = chaserModel;
                behaviorParameters.BehaviorName = chaserBehaviorName;
                break;
            case Mode.Dodge:
                behaviorParameters.Model = dodgerModel;
                behaviorParameters.BehaviorName = dodgerBehaviorName;
                break;
            default:
                break;
        }
        gameObject.GetComponent<Agent>().LazyInitialize();
	}

    private PacManAction GetDeterministicCollectAction()
    {
        Vector3 capsuleFlowDirection = _agentCoordinator.GetFoodFlowDirection(transform.position);
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        Debug.Log($"Collect action: {droneAction.Acceleration}");

        return droneAction;
    }

    private PacManAction GetDeterministicBorderAction()
    {
        Vector3 borderFlowDirection = _agentCoordinator.GetBorderFlowDirection(transform.position);
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(borderFlowDirection.x, borderFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };

        return droneAction;
    }

    private PacManAction getDeterministicCapsuleAction()
    {
        Vector3 capsuleFlowDirection = _agentCoordinator.GetCapsuleFlowDirection(transform.position);
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        Debug.Log($"Capsule action: {droneAction.Acceleration}");

        return droneAction;
    }

    private void chooseMode()
    {
        if (_agentCoordinator == null || _agent == null)
        {
            return;
        }

        var allEnemies = _agentCoordinator.GetAggregatedEnemyObservations();
        float minEnemyDistance = float.MaxValue;
        bool hasEnemyEstimate = allEnemies.Count > 0;
        foreach(var obs in allEnemies)
        {
            float dist = Vector3.Distance(transform.position, obs.Position);
            if(dist < minEnemyDistance)
                minEnemyDistance = dist;
        }

        bool enemyIsClose = hasEnemyEstimate && minEnemyDistance <= chaseDistanceThreshold;

        if (_agent.GetCarriedFoodCount() > foodRetreatThreshold)
            ApplySwitch(Mode.Retreat);
        else if (enemyIsClose && _agent.isGhost && !_agent.isScared)
            ApplySwitch(Mode.Chase);
        else if (enemyIsClose && (!_agent.isGhost || _agent.isScared))
            ApplySwitch(Mode.Dodge);
        else
            ApplySwitch(Mode.Collect);
    }

    public void OnDrawGizmos()
    {
        // Draw a little text above the agent to indicate current mode
        Vector3 textPosition = transform.position + Vector3.up * 2f;
        UnityEditor.Handles.Label(textPosition, currentMode.ToString());
    }
}
