using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
public class TeamManager : MonoBehaviour
{
    public static TeamManager Instance = null;
    private SimpleMultiAgentGroup redTeam = new SimpleMultiAgentGroup(), blueTeam =  new SimpleMultiAgentGroup();
    [SerializeField] private GameObject agentContainer;
    private List<Agent> registeredReds = new List<Agent>(), registeredBlues = new List<Agent>();
    public int redAgents => registeredReds.Count;
    public int blueAgents => registeredBlues.Count;
    private void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
        }
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void RegisterAll()
    {
        foreach (Agent agent in agentContainer.GetComponentsInChildren<Agent>())
        {
            
            if (agent.gameObject.CompareTag("Red"))
            {

                redTeam.RegisterAgent(agent);
                registeredReds.Add(agent);
            }

            if (agent.gameObject.CompareTag("Blue"))
            {

                blueTeam.RegisterAgent(agent);
                registeredBlues.Add(agent);
            }
        }
        
        Debug.Log($"Registered {redAgents} reds and  {blueAgents} blues.");
    }

    public void DeRegisterAll()
    {
        foreach (Agent agent in registeredReds)
        {
            redTeam.UnregisterAgent(agent);
            //Debug.Log("Deregistered red agent");
        }
        foreach (Agent agent in registeredBlues)
        {
            blueTeam.UnregisterAgent(agent);
            //Debug.Log("Deregistered blue agent");
        }
        registeredReds.Clear();
        registeredBlues.Clear();
        Debug.Log($"All agents registered: {registeredReds.Count} reds and {registeredBlues.Count} blues.");
    }

    public void RewardEndOfGame(bool blueWin)
    {
        if (blueWin)
        {
            blueTeam.SetGroupReward(1f);
            redTeam.SetGroupReward(-1f);
            Debug.Log("Game ended in favour of blue team");
        }
        else
        {
            blueTeam.SetGroupReward(-1f);
            redTeam.SetGroupReward(1f);
            Debug.Log("Game ended in favour of red team");
        }
        
        
        blueTeam.EndGroupEpisode();
        redTeam.EndGroupEpisode();
    }
    
}
