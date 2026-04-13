using System;
using UnityEngine;

namespace PacMan.Interface
{
    namespace PacMan
    {
        [Serializable]
        public struct PacManAction
        {
            public Vector2 Acceleration;
            public int Index;
        }

        [Serializable]
        public struct PacManObservations
        {
            public PacManObservation[] Observations;
            public int Index;
            public float ObservationFixedTime;
            public int AgentServerIndex;
        }

        [Serializable]
        public struct PacManObservation
        {
            public Vector3 Position;
            public bool IsGhost;
            public bool Visible;
            public float ReadingDispersion;
            public bool HasFood;
            public int ServerIndex;
            public int LastRespawnStep;
        }
    }
}
