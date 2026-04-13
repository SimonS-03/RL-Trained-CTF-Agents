using System.Linq;
using PacMan.Agent;
using PacMan.Interface.PacMan;
using PacMan.Local;
using PacMan.RLAgent;
using Scripts.Map;
using Scripts.VecEnv.Inference;
using Scripts.VecEnv.Message;
using Unity.InferenceEngine;
using UnityEngine;

namespace PacMan.RLExample
{
    public class PacManRLInference : PacManAI
    {
        private const int RlObservationContinuousSize = 98;
        private InferenceHelper helper;
        private AgentCoordinator coordinator;
        public ModelAsset modelAsset;

        public override void Initialize(MapManager mapManager)
        {
            _agent = GetComponent<PacManAgentManager>();
            helper = new InferenceHelper(modelAsset);

            // Mirror PacManRLAgent team-based coordinator lookup so observations use shared team context.
            var isBlueTeam = TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue;
            coordinator = transform.parent != null
                ? transform.parent
                    .GetComponentsInChildren<AgentCoordinator>()
                    .ToList()
                    .Find(c => c.isBlueTeam == isBlueTeam)
                : GetComponentsInParent<AgentCoordinator>()
                    .ToList()
                    .Find(c => c.isBlueTeam == isBlueTeam);

            if (coordinator == null)
            {
                Debug.LogError("PacManRLInference could not find AgentCoordinator for this team.");
            }
        }

        public override PacManAction Tick()
        {
            if (helper != null && coordinator != null)
            {
                var action = helper.DoInference(
                    PacManRLAgent.CollectObservation(new AgentObservation(RlObservationContinuousSize, 0), _agent, coordinator)
                );

                if (action.Continuous != null && action.Continuous.Length >= 2)
                {
                    return new PacManAction
                    {
                        Acceleration = new Vector2(
                            Mathf.Clamp(action.Continuous[0], -1f, 1f),
                            Mathf.Clamp(action.Continuous[1], -1f, 1f)
                        ),
                    };
                }

                Debug.LogWarning("PacManRLInference model output was missing continuous actions; falling back to manual input.");
            }


            var x = 0;
            var z = 0;

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("w") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("up"))
            {
                z = 1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("a") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("left"))
            {
                x = -1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("s") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("down"))
            {
                z = -1;
            }

            if (TeamAssignmentUtil.CheckTeam(gameObject) == Team.Blue && Input.GetKey("d") || TeamAssignmentUtil.CheckTeam(gameObject) == Team.Red && Input.GetKey("right"))
            {
                x = 1;
            }

            var droneAction = new PacManAction
            {
                Acceleration = new Vector2(x, z),
            };

            return droneAction;
        }
    }
}