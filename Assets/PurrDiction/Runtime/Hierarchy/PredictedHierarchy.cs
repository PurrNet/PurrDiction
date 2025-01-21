using System;
using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly int prefabId;
        public readonly PredictedObjectID instanceId;
        
        public InstanceDetails(int prefabId, PredictedObjectID instanceId)
        {
            this.prefabId = prefabId;
            this.instanceId = instanceId;
        }

        public bool Equals(InstanceDetails other)
        {
            return prefabId == other.prefabId && instanceId.Equals(other.instanceId);
        }
        
        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, instanceId);
        }
    }
    
    public struct PredictedHierarchyState : IPredictedData<PredictedHierarchyState>
    {
        public DisposableList<InstanceDetails> spawnedPrefabs;
        public readonly int nextInstanceId;
        
        public PredictedHierarchyState(DisposableList<InstanceDetails> spawnedPrefabs, int nextInstanceId)
        {
            this.spawnedPrefabs = spawnedPrefabs;
            this.nextInstanceId = nextInstanceId;
        }

        public void Dispose() => spawnedPrefabs.Dispose();

        public override string ToString()
        {
            if (spawnedPrefabs.isDisposed)
                return $"PredictedHierarchyState(actions=DISPOSED, nextInstanceId={nextInstanceId})";
            
            string actions = string.Empty;
            for (var i = 0; i < spawnedPrefabs.Count; i++)
            {
                var details = spawnedPrefabs[i];
                actions += $"({details.prefabId}, {details.instanceId})";
                if (i < spawnedPrefabs.Count - 1)
                    actions += ", ";
            }
            
            return $"PredictedHierarchyState(actions=[{actions}], nextInstanceId={nextInstanceId})";
        }
    }
    
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<InstanceDetails> _spawnedPrefabs = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        
        private int _nextInstanceId;

        protected override void UpdateUnityState(ref PredictedHierarchyState state)
        {
            int count = _spawnedPrefabs.Count;
            var copy = new DisposableList<InstanceDetails>(count);
            for (var i = 0; i < count; i++)
                copy.Add(_spawnedPrefabs[i]);
            
            state = new PredictedHierarchyState(copy, _nextInstanceId);
        }

        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            base.Setup(manager, world);
            var s = settings;
            s.interpolate = false;
            s.autoIncludeTransform = false;
            settings = s;
        }

        protected override void RollbackUnityState(PredictedHierarchyState state)
        {
            var currentActions = _spawnedPrefabs.Count;
            var stateActions = state.spawnedPrefabs.Count;
            
            var min = Mathf.Min(currentActions, stateActions);
            
            int i;
            
            for (i = 0; i < min; i++)
            {
                var current = _spawnedPrefabs[i];
                var target = state.spawnedPrefabs[i];
                
                if (!current.Equals(target))
                    break;
            }
            
            // we match up to i, so we need to undo the rest of the actions
            int countToUndo = currentActions - i;

            if (countToUndo > 0)
            {
                for (var j = currentActions - 1; j >= i; j--)
                {
                    var details = _spawnedPrefabs[j];
                    if (!_instanceMap.Remove(details.instanceId, out var instance))
                        predictionManager.InternalDelete(instance);
                }

                // clear the undone actions
                _spawnedPrefabs.RemoveRange(i, countToUndo);
            }
            
            // we need to redo the rest of the actions
            for (var j = i; j < stateActions; j++)
            {
                var details = state.spawnedPrefabs[j];
                var pid = details.prefabId;
                
                if (predictionManager.TryGetPrefab(pid, out var prefab))
                {
                    var go = predictionManager.InternalCreate(prefab);
                    var id = details.instanceId;
                    
                    _instanceMap.Add(id, go);
                    _spawnedPrefabs.Add(details);
                }
                else PurrLogger.LogError($"Mismatch: Failed to find prefab {pid}");
            }
            
            _nextInstanceId = state.nextInstanceId;

            if (_spawnedPrefabs.Count != state.spawnedPrefabs.Count)
                PurrLogger.LogError($"Mismatch: Action count {_spawnedPrefabs.Count} != {state.spawnedPrefabs.Count}");
        }
        
        public PredictedObjectID? Create(int prefabId)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;
            
            return Create(prefab);
        }

        public PredictedObjectID? Create(GameObject prefab)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var prefabId))
                return default;
            
            var go = predictionManager.InternalCreate(prefab);
            var id = new PredictedObjectID(_nextInstanceId);
            var details = new InstanceDetails(prefabId, id);
            
            _instanceMap.Add(id, go);
            _spawnedPrefabs.Add(details);
            _nextInstanceId++;
            
            return id;
        }
        
        public bool TryCreate(int prefabId, out PredictedObjectID id)
        {
            var result = Create(prefabId);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }
        
        public bool TryCreate(GameObject prefab, out PredictedObjectID id)
        {
            var result = Create(prefab);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }
        
        public bool TryGetGameObject(PredictedObjectID? id, out GameObject go)
        {
            if (!id.HasValue)
            {
                go = null;
                return false;
            }
            
            return _instanceMap.TryGetValue(id.Value, out go);
        }
        
        public void Delete(PredictedObjectID id)
        {
            if (!_instanceMap.Remove(id, out var instance))
                return;

            var count = _spawnedPrefabs.Count;
            for (var i = 0; i < count; i++)
            {
                if (_spawnedPrefabs[i].instanceId.Equals(id))
                {
                    _spawnedPrefabs.RemoveAt(i);
                    break;
                }
            }
            
            predictionManager.InternalDelete(instance);
        }
        
        public void Delete(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return;
            
            Delete(id.Value);
        }
    }
}
