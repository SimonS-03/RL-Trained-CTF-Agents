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
        Collect
    }

	[Header("Trigger")]
	[SerializeField] private bool useAlternateBehavior;
	[SerializeField] private bool applyOnStart = true;
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
            default:
                break;
        }
    }

	private void ApplySwitch(Mode mode)
	{
        if(currentMode == mode)
            return;
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
        // Determine team before that !! by using tag surely
        Vector3 capsuleFlowDirection = AgentCoordinator.Instance.GetCapsuleFlowDirection(transform.position, gameObject.CompareTag("Blue"));
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };

        return droneAction;
    }
}
