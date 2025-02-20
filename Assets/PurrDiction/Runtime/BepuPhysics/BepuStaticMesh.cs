using System;
using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.CollisionRuleManagement;
using ConversionHelper;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/BEPU/Bepu Static Mesh Collider")]
    public class BepuStaticMesh : MonoBehaviour
    {
        [SerializeField] private Mesh _mesh;
        [SerializeField] private bool _isTrigger;
        [SerializeField] private bool _drawGizmos;
        
        public event BepuCollisionHandler.TriggerEventHandler onTriggerEnter;
        public event BepuCollisionHandler.TriggerEventHandler onTriggerExit;
        public event BepuCollisionHandler.CollisionEventHandler onCollisionEnter;
        public event BepuCollisionHandler.CollisionEventHandler onCollisionExit;

        private StaticMesh _staticMesh;
        private BEPUphysics.Space _space;
        private PredictionManager _predictionManager;
        private BepuCollisionHandler _collisionHandler;

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
            InitializeCollisionHandler();
            UpdateTriggerState();
            
#if UNITY_EDITOR
            var debugger = FindFirstObjectByType<BepuDebugger>(FindObjectsInactive.Include);
            debugger?.RegisterStaticMesh(_staticMesh);
#endif
        }

        private void OnDestroy()
        {
            if (_collisionHandler != null && _staticMesh != null)
            {
                _collisionHandler.UnsubscribeFromEvents(_staticMesh);
            }
            
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
            _staticMesh.Tag = gameObject;
            _space.Add(_staticMesh);
        }
        
        private void InitializeCollisionHandler()
        {
            _collisionHandler = new BepuCollisionHandler(_predictionManager, _isTrigger, gameObject);
            _collisionHandler.onTriggerEnter += (go) => onTriggerEnter?.Invoke(go);
            _collisionHandler.onTriggerExit += (go) => onTriggerExit?.Invoke(go);
            _collisionHandler.onCollisionEnter += (go) => onCollisionEnter?.Invoke(go);
            _collisionHandler.onCollisionExit += (go) => onCollisionExit?.Invoke(go);
        }

        private void UpdateTriggerState()
        {
            if (_staticMesh == null) return;

            _collisionHandler.SubscribeToEvents(_staticMesh);
    
            _staticMesh.CollisionRules.Personal = _isTrigger ? 
                CollisionRule.NoSolver : 
                CollisionRule.Normal;
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
