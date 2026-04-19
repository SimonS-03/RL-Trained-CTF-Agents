using Unity.InferenceEngine;
using UnityEngine;
using Unity.MLAgents.Policies;
using Unity.MLAgents;
using PacMan.Agent;
using PacMan.Interface.PacMan;
using PacMan.RLAgent;
using Unity.AppUI.MVVM;

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
    private PacManAction _inferenceAction;

	private void Awake()
	{
		behaviorParameters = GetComponent<BehaviorParameters>();

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
        ApplySwitch(currentMode);
        ApplyAction();
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
        
        Vector3 capsuleFlowDirection = AgentCoordinator.Instance.GetFoodFlowDirection(transform.position, gameObject.CompareTag("Blue"));
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        Debug.Log($"Collect action: {droneAction.Acceleration}");

        return droneAction;
    }

    private PacManAction GetDeterministicBorderAction()
    {
        Vector3 borderFlowDirection = AgentCoordinator.Instance.GetBorderFlowDirection(transform.position, gameObject.CompareTag("Blue"));
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(borderFlowDirection.x, borderFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };

        return droneAction;
    }

    private PacManAction getDeterministicCapsuleAction()
    {
        Vector3 capsuleFlowDirection = AgentCoordinator.Instance.GetCapsuleFlowDirection(transform.position, gameObject.CompareTag("Blue"));
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        Debug.Log($"Capsule action: {droneAction.Acceleration}");

        return droneAction;
    }
}
