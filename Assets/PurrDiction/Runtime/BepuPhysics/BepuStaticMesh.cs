using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using ConversionHelper;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class BepuStaticMesh : MonoBehaviour
    {
        [SerializeField] private Mesh mesh;

        private StaticMesh _staticMesh;
        private BEPUphysics.Space _space;

        private void Start()
        {
            if (!mesh.isReadable)
            {
                PurrLogger.LogError($"Can't handle static mesh {mesh.name} because it is not readable! (GameObject: {gameObject.name}) " +
                                    $"\n <color=yellow>Please click on the imported model and enable `Read/Write` and apply the settings</color>", this);
                return;
            }
            
            _space = FindFirstObjectByType<PredictionManager>().physics;
            if (_space == null)
            {
                PurrLogger.LogException($"No physics space found in scene!", this);
                return;
            }
            CreateEntity();
#if UNITY_EDITOR
            FindFirstObjectByType<BepuDebugger>()?.RegisterStaticMesh(_staticMesh);
#endif
        }

        private void OnDestroy()
        {
            if (_space != null && _staticMesh != null)
            {
                _space.Remove(_staticMesh);
            }
        }

        private void CreateEntity()
        {
            var indices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                indices.AddRange(mesh.GetIndices(i));
            }

            var worldVertices = mesh.vertices;
            for (int i = 0; i < worldVertices.Length; i++)
            {
                worldVertices[i] = transform.TransformPoint(worldVertices[i]);
            }

            _staticMesh = new StaticMesh(MathConverter.Convert(worldVertices), indices.ToArray());
            _space.Add(_staticMesh);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (mesh == null) return;

            var worldVertices = mesh.vertices;
            for (int i = 0; i < worldVertices.Length; i++)
            {
                worldVertices[i] = transform.TransformPoint(worldVertices[i]);
            }

            var indices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                indices.AddRange(mesh.GetIndices(i));
            }

            Gizmos.color = Color.green;
            for (int i = 0; i < indices.Count; i += 3)
            {
                Vector3 v0 = worldVertices[indices[i]];
                Vector3 v1 = worldVertices[indices[i + 1]];
                Vector3 v2 = worldVertices[indices[i + 2]];

                Gizmos.DrawLine(v0, v1);
                Gizmos.DrawLine(v1, v2);
                Gizmos.DrawLine(v2, v0);
            }
        }
#endif
    }
}
