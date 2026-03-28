using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Scripts.Map;
using PacMan.Game;

namespace PacMan.RLAgent
{
    public class AgentCoordinator : MonoBehaviour
    {
        private PacManGameManager PacManGameManager;

        [SerializeField]
        private Vector3 cellScale = Vector3.one;
        public ObstacleMap ObstacleMap { get; private set; }
        private FlowField ff;
        
        public bool drawFlowField = false;

        public void Start()
        {
            PacManGameManager = FindFirstObjectByType<PacManGameManager>();

            ObstacleMap = ObstacleMap.Initialize(
                PacManGameManager.mapManager,
                new List<GameObject>(),
                cellScale
            );
            
            BuildFlowFields();
        }

        void FixedUpdate()
        {
            // Debug.Log(PacManGameManager.foodList.Select(obj => obj.gameObject).ToList().Count);
        }

        private void BuildFlowFields()
        {
            List<Vector3> goals = PacManGameManager.foodList.Select(obj => obj.gameObject.transform.position).ToList();
            ff = FlowField.Build(ObstacleMap, goals);
            Debug.Log(ff);
        }

        public Vector3 GetFlowDirection(Vector3 worldPos)
        {
            if (ff == null) return Vector3.zero;
            return ff.GetDirection(worldPos);
        }

        public float GetFlowCost(Vector3 worldPos)
        {
            if (ff == null) return float.MaxValue;
            return ff.GetCost(worldPos);
        }

        void OnDrawGizmos() {
            if(drawFlowField) {
                Debug.Log("Drawing ff");
                foreach (var pos in ObstacleMap.traversabilityPerCell) {
                    //Debug.Log(pos);
                    var position = new Vector3Int(pos.Key.x, 0, pos.Key.y);

                    var cellToWorld = ObstacleMap.CellToWorld(position) + ObstacleMap.trueScale / 2;
                    // cellToWorld.y = 0;
                    cellToWorld += Vector3.up * 0.25f;

                    // var scale = Vector3.Scale(transform.localScale, ObstacleMap.trueScale);
                    // var gizmoSize = new Vector3(scale.x * 0.95f, 0.005f, scale.z * 0.95f);
                    
                    Gizmos.color = Color.Lerp(Color.green, Color.red, GetFlowCost(cellToWorld) / 200f);
                    var dir = GetFlowDirection(cellToWorld);
                    Debug.Log(dir);
                    Gizmos.DrawRay(cellToWorld, dir.normalized * 3f);

                    // Gizmos.DrawCube(cellToWorld, gizmoSize);
                }
            }
        }


    }
}
