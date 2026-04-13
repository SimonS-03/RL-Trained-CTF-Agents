using PacMan.Game;
using PacMan.Local;
using Scripts.VecEnv.Core;
using UnityEngine;
using Random = UnityEngine.Random;
namespace PacMan.RLExample
{
    public class RLTrainGameManager : PacManGameManager
    {
        private bool registeredRestartDelegate = false;

        public void Start()
        {
            base.Start();
            AutomaticRestart = false;
        }

        public void FixedUpdate()
        {
            base.FixedUpdate();
            if (finished && !registeredRestartDelegate)
            {
                registeredRestartDelegate = true;
                GymVecEnvManager.Instance.PreStepReset += RestartGame;
            }
        }

        public override void RestartGame()
        {
            base.RestartGame();
            GymVecEnvManager.Instance.PreStepReset -= RestartGame;
            registeredRestartDelegate = false;
        }

        public override void RespawnAgentAtStart(PacManAgentManager agent)
        {
            base.RespawnAgentAtStart(agent);
            
            var localRandomPosition = new Vector3(Random.Range(-13f, -2.5f), 0.4f, Random.Range(-5f, 5f));
            agent.transform.position = transform.TransformPoint(localRandomPosition);
        }
    }
}