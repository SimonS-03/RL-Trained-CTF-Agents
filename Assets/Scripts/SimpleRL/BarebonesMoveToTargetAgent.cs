using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace SimpleRL
{
    /// <summary>
    /// Fully decoupled ML-Agents smoke-test agent.
    /// Uses constant 0f observations and rewards near-zero actions.
    /// </summary>
    public class BarebonesMoveToTargetAgent : Agent
    {
        [Header("Reward Settings")]
        [SerializeField]
        private float stepPenalty = -0.001f;
        [SerializeField]
        private float actionMagnitudePenalty = 0.02f;
        [SerializeField]
        private float stillBonus = 0.01f;
        [SerializeField]
        private float stillThreshold = 0.05f;
        [SerializeField]
        private int stillStepsToFinish = 40;
        [SerializeField]
        private float successReward = 0.5f;

        private int _consecutiveStillSteps;

        public override void OnEpisodeBegin()
        {
            _consecutiveStillSteps = 0;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            // Intentionally constant sample inputs for smoke testing.
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            var x = actions.ContinuousActions[0];
            var z = actions.ContinuousActions[1];
            var magnitude = Mathf.Sqrt((x * x) + (z * z));

            ApplyRewardFromAction(magnitude);
        }

        private void ApplyRewardFromAction(float magnitude)
        {
            AddReward(stepPenalty);
            AddReward(-actionMagnitudePenalty * magnitude);

            if (magnitude <= stillThreshold)
            {
                _consecutiveStillSteps++;
                AddReward(stillBonus);
            }
            else
            {
                _consecutiveStillSteps = 0;
            }

            if (_consecutiveStillSteps >= stillStepsToFinish)
            {
                AddReward(successReward);
                EndEpisode();
            }
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuous = actionsOut.ContinuousActions;
            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");
        }
    }
}
