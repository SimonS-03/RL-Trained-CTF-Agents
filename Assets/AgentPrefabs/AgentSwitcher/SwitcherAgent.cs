using System.Collections.Generic;
using System.Linq;
using Imported.StandardAssets.CrossPlatformInput.Scripts;
using PacMan.Agent;
using PacMan.Game;
using PacMan.Interface.PacMan;
using PacMan.Local;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

public class SwitcherAgent: Unity.MLAgents.Agent
{
    [SerializeField] private PacManAgentManager _agent;
    private List<PacManAgentManager> allFriendlies =  new List<PacManAgentManager>();
    public Dictionary<int, Vector3> lastHeard = new Dictionary<int, Vector3>();
    private const int EntityObservationSize = 7; // size of an agent observation
    private Switcher _switcher;

    public override void Initialize()
    {
        _agent = GetComponentInParent<PacManAgentManager>();
        _switcher = GetComponentInParent<Switcher>();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if(_agent == null)
        {
            _agent = GetComponent<PacManAgentManager>();
        }
        if(_switcher == null)
        {
            _switcher = GetComponent<Switcher>();
        }

        var observations = _agent.GetEnemyObservations();
        List<PacManAgentManager> visibleEnemies = _agent.GetVisibleEnemyAgents(); 
        var filteredObs = filterObservation(observations, visibleEnemies);
        
        //Debug.Log("Visible count: " + visibleEnemies.Count + " hidden count: " + filteredObs.Count);
        
        // Here we only input the magnitude of velocity since we are always heading in our viewed direction
        float velMag = _agent.GetVelocity().magnitude / 3f; // Assuming 3 is about max speed
        sensor.AddObservation(velMag);

        allFriendlies = _agent.GetFriendlyAgents();
        
        int enemyBufferSize = AppendFixedSizeEntityBuffer(sensor, visibleEnemies, filteredObs, 4);
        int friendlyBufferSize = AppendFixedSizeEntityBuffer(sensor, allFriendlies, null, 3);
    }
    
    public override void OnActionReceived(ActionBuffers actions)
    {
        float horizontal = actions.ContinuousActions[0];
        float vertical = actions.ContinuousActions[1];
        Vector3 acceleration = new Vector3(horizontal, 0f, vertical);
        acceleration = transform.TransformDirection(acceleration).normalized; // Convert from local to world space
        if (_switcher != null)
        {
            _switcher.ReceiveActionFromAgent(new PacManAction
            {
                Acceleration = new Vector2(acceleration.x, acceleration.z)
            });
        }
        else
        {
            Debug.LogWarning("Switcher component not found on agent. Actions will not be applied.");
            _switcher = GetComponent<Switcher>();
        }
    }

    private float[] BuildEntityObservation(PacManAgentManager other, int isReal = 1)
    {
        Vector3 rawDiffPos = other.transform.position - _agent.transform.position;
        Vector3 direction = transform.InverseTransformDirection(rawDiffPos.normalized); 
        float distance = rawDiffPos.magnitude;
        float normalizedDistance = Mathf.Clamp(distance / 12.5f, 0f, 1f);
        
        Vector3 relativeVel = transform.InverseTransformDirection(other.GetVelocity() - _agent.GetVelocity());
    
        // Note: If max speed is 3, max RELATIVE speed is 6 (crashing head-on). 
        // Divide by 6f to keep it properly between -1 and 1.
        Vector3 normalizedRelVel = relativeVel / 6f;
        float isVisible = 1f;
        return new[]
        {
            direction.x,                     
            direction.z,                     
            normalizedDistance,              
            Mathf.Clamp(normalizedRelVel.x, -1f, 1f),
            Mathf.Clamp(normalizedRelVel.z, -1f, 1f),
            isVisible,
            isReal,
        };
    }

    private int AppendFixedSizeEntityBuffer(VectorSensor sensor, List<PacManAgentManager> entities, List<float[]> observations, int fixedNbrEntities)
    {
        List<float[]> enemyArrays = entities.Select(e => BuildEntityObservation(e)).ToList(); ;
        if (observations != null)
        {
            enemyArrays.AddRange(observations);
        }
        enemyArrays = enemyArrays.OrderBy(e => e[2]).ToList();

        int realCount = enemyArrays.Count;
        for (int i = 0; i < realCount; i++)
        {
            sensor.AddObservation(enemyArrays[i]);
        }
        
        // If we have less than 4 agents, seen or unseen.
        for (int i = realCount; i < fixedNbrEntities; i++)
        {
            sensor.AddObservation(new float[EntityObservationSize]);
        }

        return realCount;
    }

    
    /// <summary>
    /// Gets a list of obervations from pacManObservations that has filtered out visible enemies
    /// </summary>
    /// <param name="observations"></param>
    /// <returns></returns>
    public List<float[]> filterObservation(PacManObservations observations, List<PacManAgentManager> visibleEnemies)
    {
        List<int> visibleIndices = visibleEnemies.Select(a => a.serverIndex).ToList();

        foreach (var obs in visibleEnemies)
        {
            lastHeard[obs.serverIndex] = obs.transform.position;
        }
        
        List<float[]> filteredObservations = new List<float[]>();
        foreach (PacManObservation obs in observations.Observations)
        {
            if (visibleIndices.Contains(obs.ServerIndex))
                continue;
            
            Vector3 worldPos;
            if (Vector3.Distance(Vector3.zero, obs.Position) < .001f) // If the agent is not making a sound
            {
                if (lastHeard.ContainsKey(obs.ServerIndex))
                {
                    //Debug.Log("Used remembered observation!");
                    worldPos = lastHeard[obs.ServerIndex];
                    //Debug.Log($"Used remembered observation:  {worldPos}");
                }
                else
                {
                    continue; // If we have never heard this agent, we have no information about it, so skip the observation.
                }   
            }
            else
            {
                worldPos = transform.parent.parent.TransformPoint(obs.Position);
                lastHeard[obs.ServerIndex] = worldPos;
            }
            
            
            //Debug.Log(obs.Position);
            
            Vector3 rawDiffPos = worldPos - _agent.transform.position;
            Vector3 direction = transform.InverseTransformDirection(rawDiffPos.normalized); 
            float distance = rawDiffPos.magnitude;
            float normalizedDistance = Mathf.Clamp(distance / 12.5f, 0f, 1f);
        
            // Assuming observations do not have a velocity 
            Vector3 relativeVel = transform.InverseTransformDirection(Vector3.zero - _agent.GetVelocity());
    
            // Note: If max speed is 3, max RELATIVE speed is 6 (crashing head-on). 
            // Divide by 6f to keep it properly between -1 and 1.
            Vector3 normalizedRelVel = relativeVel / 6f; 
            
            
            float[] newObs = new []
            {
                direction.x,
                direction.z,
                normalizedDistance,
                Mathf.Clamp(normalizedRelVel.x, -1f, 1f),
                Mathf.Clamp(normalizedRelVel.z, -1f, 1f),
                0f, // Not visible
                1f // But a real observation
            };
            
            //Debug.Log(newObs[2]);
            filteredObservations.Add(newObs);
        }

        return filteredObservations;
    }
    
    public virtual void OnCollisionStay(Collision other)
    {
        var otherAgent = other.gameObject.GetComponent<PacManAgentManager>();
        
        if (otherAgent != null && other.gameObject.tag != tag && _agent.IsGhost() && !_agent.isScared && !otherAgent.IsGhost())
        {
            //tagged other
            lastHeard.Remove(otherAgent.serverIndex); // Remove from memory so that we don't keep "remembering" a dead agent's position
            foreach (PacManAgentManager friendly in _agent.GetFriendlyAgents())
            {
                LegalChaser legalChaser = friendly.GetComponent<LegalChaser>();
                if (legalChaser != null)
                {
                    legalChaser.lastHeard.Remove(otherAgent.serverIndex);
                }
            }
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        
        continuousActions[0] = CrossPlatformInputManager.GetAxis("Horizontal");
        continuousActions[1] = CrossPlatformInputManager.GetAxis("Vertical");
    }
}
