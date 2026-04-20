using Unity.InferenceEngine;
using UnityEngine;
using Unity.MLAgents.Policies;
using Unity.MLAgents;
using PacMan.Agent;
using PacMan.Interface.PacMan;
using PacMan.RLAgent;
using PacMan.Local;
using Unity.MLAgents.Sensors;
using UnityEngine.Serialization;

public class Switcher : MonoBehaviour
{
    private enum Mode
    {
        Chase,
        Dodge,
        Collect,
        Retreat,
        Capsule,
        Border
    }

    [SerializeField] private Mode currentMode = Mode.Chase;

    
	[Header("Dodging and Chasing")]
    [SerializeField] private SwitcherAgent switcherAgent;
    [SerializeField] private BehaviorParameters behaviorParametersChaser;
	[SerializeField] private ModelAsset dodgerModel;
	[SerializeField] private string dodgerBehaviorName = "dodger";
    [SerializeField] private ModelAsset chaserModel;
    [SerializeField] private string chaserBehaviorName = "chaser";

    [Header("Border Warfare")] 
    [SerializeField] private BorderSwitcherAgent borderSwitcherAgent;
    [SerializeField] private ModelAsset bwModel;
    [SerializeField] private string bwBehaviorName = "border_warfare";
    [SerializeField] private BehaviorParameters borderBehaviorParameters;

    [Header("Subagents")] 
    [SerializeField] private GameObject chaserAgentObject;
    [SerializeField] private GameObject borderAgentObject;
    
	private ModelAsset originalModel;
	private string originalBehaviorName;
    private PacManAI _pacManAI;
    private PacManAgentManager _agent;
    private AgentCoordinator _agentCoordinator;
    private AgentCoordinator _enemyCoordinator;
    private PacManAction _inferenceAction;
    private int foodRetreatThreshold = 3;
    private float chaseDistanceThreshold = 10f;
    private float dodgeThreshold = 5f;
    private float capsuleDistanceThreshold = 5f;

	private void Awake()
	{
		//behaviorParameters_chaser = GetComponent<BehaviorParameters>();
        _agent = GetComponent<PacManAgentManager>();

		if (behaviorParametersChaser == null)
		{
			Debug.LogWarning($"{nameof(Switcher)} requires a {nameof(BehaviorParameters)} on {name}.", this);
			enabled = false;
			return;
		}

		originalModel = behaviorParametersChaser.Model;
		originalBehaviorName = behaviorParametersChaser.BehaviorName;
	}

    public void Initialize()
    {
        _pacManAI = GetComponent<PacManAI>();
        if (gameObject.CompareTag("Blue"))
        {
            _agentCoordinator = AgentCoordinator.BlueInstance;
            _enemyCoordinator = AgentCoordinator.RedInstance;
        }

        else
        {
            _agentCoordinator = AgentCoordinator.RedInstance;
            _enemyCoordinator = AgentCoordinator.BlueInstance;
        }
            
    }

    public void ReceiveActionFromAgent(PacManAction action)
    {
        if(_pacManAI == null)
        {
            Debug.Log("PacManAI null");
            _pacManAI = GetComponent<PacManAI>();
            return;
        }
        _inferenceAction = action;
    }

	private void Update()
	{
        if(_pacManAI == null || _agent == null || _agentCoordinator == null || behaviorParametersChaser == null)
        {
            _pacManAI = GetComponent<PacManAI>();
            _agent = GetComponent<PacManAgentManager>();
            behaviorParametersChaser = transform.Find("ChaseAgent").GetComponent<BehaviorParameters>();
            borderBehaviorParameters = transform.Find("BorderAgent").GetComponent<BehaviorParameters>();
            if (gameObject.CompareTag("Blue"))
            {
                _agentCoordinator = AgentCoordinator.BlueInstance;
                _enemyCoordinator = AgentCoordinator.RedInstance;
            }
            else
            {
                _agentCoordinator = AgentCoordinator.RedInstance;
                _enemyCoordinator = AgentCoordinator.BlueInstance;
            }
            Debug.Log("Some components are null atm");
            return;
        }
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
            case Mode.Border:
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
                borderAgentObject.SetActive(false);
                chaserAgentObject.SetActive(true);
                behaviorParametersChaser.Model = chaserModel;
                behaviorParametersChaser.BehaviorName = chaserBehaviorName;
                //gameObject.GetComponent<Agent>().LazyInitialize();
                break;
            case Mode.Dodge:
                borderAgentObject.SetActive(false);
                chaserAgentObject.SetActive(true);
                behaviorParametersChaser.Model = dodgerModel;
                behaviorParametersChaser.BehaviorName = dodgerBehaviorName;
                //gameObject.GetComponent<Agent>().LazyInitialize();
                break;
            case Mode.Border:
                chaserAgentObject.SetActive(false);
                borderAgentObject.SetActive(true);
                Debug.Log("Using border!!!!");
                // We dont assign the model/behaviourName since it never switches for the borderAgent

                //gameObject.GetComponent<Agent>().LazyInitialize();
                break;
            default:
                break;
        }
        
	}

    private PacManAction GetDeterministicCollectAction()
    {
        Vector3 capsuleFlowDirection = _agentCoordinator.GetFoodFlowDirection(transform.position);
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        //Debug.Log($"Collect action: {droneAction.Acceleration}");

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
        Vector3 capsuleFlowDirection = _agentCoordinator.GetCapsuleFlowDirection(transform.position).normalized;
        var droneAction = new PacManAction
        {
            Acceleration = new Vector2(capsuleFlowDirection.x, capsuleFlowDirection.z), // Controller converts to normalized if magnitude > 1. Magnitude 0.3 guarantees not observed
        };
        //Debug.Log($"Capsule action: {droneAction.Acceleration}");

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
        bool enemyIsCloseDodge = hasEnemyEstimate && minEnemyDistance <= dodgeThreshold;
        var distanceToNearestCapsule = _agentCoordinator.GetCapsuleFlowCost(transform.position);

        if (_agent.GetCarriedFoodCount() > foodRetreatThreshold)
            ApplySwitch(Mode.Retreat);
        else if (enemyIsClose && _agent.isGhost && !_agent.isScared && Mathf.Abs(transform.position.x) > 1.5f)
            ApplySwitch(Mode.Chase);
        else if (enemyIsCloseDodge && (!_agent.isGhost || _agent.isScared) && Mathf.Abs(transform.position.x) > 1.5f)
            ApplySwitch(Mode.Dodge);
        else if (_enemyCoordinator.GetBorderFlowCost(transform.position) <= 4f && Mathf.Abs(transform.position.x) < 4f)
        {
            ApplySwitch(Mode.Border);
        }
        else if (distanceToNearestCapsule <= capsuleDistanceThreshold)
            ApplySwitch(Mode.Capsule);
        else
            ApplySwitch(Mode.Collect);
    }

    // public void OnDrawGizmos()
    // {
    //     // Draw a little text above the agent to indicate current mode
    //     Vector3 textPosition = transform.position + Vector3.up * 2f;
    //     UnityEditor.Handles.Label(textPosition, currentMode.ToString());
    // }
}
