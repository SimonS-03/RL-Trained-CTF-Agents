using System.Collections.Generic;
using PacMan.Interface;
using PacMan.Local;
using UnityEngine;

namespace PacMan.Game
{
    public class PacManWorker : MonoBehaviour
    {
        public GameObject CreateEdible(GameObject parent, GameObject prefab, Vector3 position)
        {
            GameObject spawn = Instantiate(prefab, position, Quaternion.identity, parent.transform);
            spawn.transform.localPosition = new Vector3(spawn.transform.localPosition.x, prefab.transform.position.y, spawn.transform.localPosition.z);
            spawn.name = prefab.name;
            return spawn;
        }

        public GameObject CreateAgent(GameObject prefab, GameObject parent, Vector3 position, string team_tag)
        {
            prefab.tag = team_tag;
            GameObject agent = Instantiate(prefab, Vector3.zero, Quaternion.identity, parent.transform);
            agent.tag = team_tag;
            agent.name = prefab.name;
            agent.transform.position = position + prefab.transform.position;

            var manager = agent.GetComponent<PacManAgentManager>();
            manager.globalStartPosition = agent.transform.position;

            return agent;
        }

        public void RemoveObject(GameObject toRemove)
        {
            Destroy(toRemove);
        }

        public void ResetAgent(GameObject agent)
        {
            var manager = agent.GetComponent<PacManAgentManager>();
            manager.foodCarried = new List<GameObject>();
            manager.isScared = false;
            manager.scaredUntil = 0;
            var movementController = agent.GetComponent<PacManMovementController>();
            if (movementController != null)
            {
                movementController.enabled = true;
            }
        }
    }
}
