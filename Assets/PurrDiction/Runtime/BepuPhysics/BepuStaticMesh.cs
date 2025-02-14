using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using ConversionHelper;
using PurrNet.Logging;
using UnityEngine;
using UnityEngine.Serialization;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/BEPU/Bepu Static Mesh Collider")]
    public class BepuStaticMesh : MonoBehaviour
    {
        [FormerlySerializedAs("mesh")] [SerializeField] private Mesh _mesh;
        [FormerlySerializedAs("drawGizmos")] [SerializeField] private bool _drawGizmos;

        private StaticMesh _staticMesh;
        private BEPUphysics.Space _space;
        private PredictionManager _predictionManager;

        private void Start()
        {
            if (!_mesh.isReadable)
            {
                PurrLogger.LogError($"Can't handle static mesh {_mesh.name} because it is not readable! (GameObject: {gameObject.name}) " +
                                    $"\n <color=yellow>Please click on the imported model and enable `Read/Write` and apply the settings</color>", this);
                return;
            }

            if (!PredictionManager.TryGetInstance(gameObject.scene.handle, out _predictionManager))
            {
                PurrLogger.LogError($"No prediction manager found in scene!", this);
                return;
            }

            if (_predictionManager.physics == null)
                _predictionManager.onPhysicsSet += OnPhysicsSet;
            else
                OnPhysicsSet();
        }

        private void OnPhysicsSet()
        {
            _space = _predictionManager.physics;
            if (_space == null)
            {
                PurrLogger.LogException($"No physics space found in scene!", this);
                return;
            }
            CreateEntity();
            _predictionManager.onPhysicsSet -= OnPhysicsSet;
            
#if UNITY_EDITOR
            var debugger = FindFirstObjectByType<BepuDebugger>(FindObjectsInactive.Include);
            debugger?.RegisterStaticMesh(_staticMesh);
#endif
        }

        private void OnDestroy()
        {
            if (_space != null && _staticMesh != null)
            {
                _space.Remove(_staticMesh);
            }
            
            if(_predictionManager)
                _predictionManager.onPhysicsSet -= OnPhysicsSet;
        }

        private void CreateEntity()
        {
            var indices = new List<int>();
            for (int i = 0; i < _mesh.subMeshCount; i++)
            {
                indices.AddRange(_mesh.GetIndices(i));
            }

            var worldVertices = _mesh.vertices;
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
            if (!_drawGizmos)
                return;
            
            if (_mesh == null) return;

            var worldVertices = _mesh.vertices;
            for (int i = 0; i < worldVertices.Length; i++)
            {
                worldVertices[i] = transform.TransformPoint(worldVertices[i]);
            }

            var indices = new List<int>();
            for (int i = 0; i < _mesh.subMeshCount; i++)
            {
                indices.AddRange(_mesh.GetIndices(i));
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
