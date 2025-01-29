using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<InstanceDetails> _spawnedPrefabs = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        readonly Dictionary<GameObject, PredictedObjectID> _goToId = new ();
        
        private uint _nextInstanceId;
        
        protected override void GetUnityState(ref PredictedHierarchyState state)
        {
            int count = _spawnedPrefabs.Count;
            var copy = new DisposableList<InstanceDetails>(count);
            for (var i = 0; i < count; i++)
                copy.Add(_spawnedPrefabs[i]);
            
            state = new PredictedHierarchyState(copy, _nextInstanceId);
        }

        protected override void SetUnityState(PredictedHierarchyState state)
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
                    if (_instanceMap.Remove(details.instanceId, out var instance) && instance)
                        Delete(details, instance);
                }

                // clear the undone actions
                _spawnedPrefabs.RemoveRange(i, countToUndo);
            }
            
            // we need to redo the rest of the actions
            for (var j = i; j < stateActions; j++)
            {
                var details = state.spawnedPrefabs[j];
                var pid = details.prefabId;
                var instanceId = details.instanceId;
                
                _nextInstanceId = instanceId.instanceId;
                
                var goId = Create(pid, details.spawnPosition, details.spawnRotation);

                if (!goId.HasValue)
                    PurrLogger.LogError($"Mismatch: Failed to create prefab {pid}");
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
        
        public PredictedObjectID? Create(int prefabId, Vector3 position, Quaternion rotation)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;
            
            return Create(prefab, position, rotation);
        }
        
        // TOOD: make a list of pools per prefab with the key not including instance id?
        //readonly Dictionary<PredictedObjectID, GameObject> _pool = new ();


        readonly Dictionary<int, PredictedTickPool> _prefabToPool = new ();
        
        private PredictedTickPool GetPool(int prefabId)
        {
            if (_prefabToPool.TryGetValue(prefabId, out var pool))
                return pool;
            
            pool = new PredictedTickPool(new List<PooledInstance>(10));
            _prefabToPool.Add(prefabId, pool);
            return pool;
        }

        protected override void UpdateView(PredictedHierarchyState interpolatedState, PredictedHierarchyState? verified)
        {
            ClearPool();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ClearPool();
        }

        private void ClearPool()
        {
            foreach (var (_, pool) in _prefabToPool)
                pool.Clear(predictionManager);
            _prefabToPool.Clear();
        }
        
        private void Delete(InstanceDetails details, GameObject go)
        {
            var pool = GetPool(details.prefabId);
            
            if (pool.Put(details, go))
            {
                go.SetActive(false);
                predictionManager.UnregisterInstance(go);
            }
            else predictionManager.InternalDelete(go);
        }

        public PredictedObjectID? Create(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (!prefab)
                return null;
            
            if (!predictionManager.TryGetPrefab(prefab, out var prefabId))
            {
                PurrLogger.LogError($"Failed to find prefab '{prefab}' in the prediction manager");
                return default;
            }
            
            var instanceId = new PredictedObjectID(_nextInstanceId);
            var key = new InstanceDetails(prefabId, instanceId, position, rotation);

            GameObject go;
            
            var pool = GetPool(prefabId);

            if (pool.TryTake(key, out var instance))
            {
                go = instance;
                go.transform.SetPositionAndRotation(position, rotation);
                predictionManager.RegisterInstance(go);
                go.SetActive(true);
            }
            else go = predictionManager.InternalCreate(prefab, position, rotation);
            
            _instanceMap.Add(instanceId, go);
            _goToId.Add(go, instanceId);
            _spawnedPrefabs.Add(key);
            _nextInstanceId++;
            
            return instanceId;
        }

        public PredictedObjectID? Create(GameObject prefab)
        {
            var trs = prefab.transform;
            trs.GetPositionAndRotation(out var position, out var rotation);
            return Create(prefab, position, rotation);
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
        
        public GameObject GetGameObject(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return null;
            
            return _instanceMap.GetValueOrDefault(id.Value);
        }
        
        public T GetComponent<T>(PredictedObjectID? id) where T : Component
        {
            if (!id.HasValue)
                return null;
            
            return _instanceMap.GetValueOrDefault(id.Value)?.GetComponent<T>();
        }
        
        public bool TryGetId(GameObject go, out PredictedObjectID id)
        {
            if (!_goToId.TryGetValue(go, out id))
                return false;
            
            return true;
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
            
            _goToId.Remove(instance);

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
