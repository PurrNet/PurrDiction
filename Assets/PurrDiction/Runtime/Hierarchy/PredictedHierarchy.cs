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
        
        private int _nextInstanceId;
        
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
                    {
                        PutInCache(details.instanceId, instance);
                        // predictionManager.InternalDelete(instance);
                    }
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
                    var id = details.instanceId;

                    GameObject go;

                    if (TryTakeFromCache(id, out var cached))
                    {
                        cached.SetActive(true);
                        go = cached;
                    }
                    else
                    {
                        go = predictionManager.InternalCreate(prefab);
                    }
                    
                    _instanceMap.Add(id, go);
                    _spawnedPrefabs.Add(details);
                }
                else PurrLogger.LogError($"Mismatch: Failed to find prefab {pid}");
            }
            
            _nextInstanceId = state.nextInstanceId;

            if (_spawnedPrefabs.Count != state.spawnedPrefabs.Count)
                PurrLogger.LogError($"Mismatch: Action count {_spawnedPrefabs.Count} != {state.spawnedPrefabs.Count}");
        }
        
        readonly Dictionary<PredictedObjectID, CachedGameObject> _tempMap = new ();

        private void Update()
        {
            foreach (var cached in _tempMap)
            {
                if (Time.time - cached.Value.time > 2f)
                {
                    predictionManager.InternalDelete(cached.Value.gameObject);
                    _tempMap.Remove(cached.Key);
                    break;
                }
            }
        }

        public PredictedObjectID? Create(int prefabId)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;
            
            return Create(prefab);
        }
        
        static readonly List<PredictedIdentity> _tempList = new();
        
        public PredictedObjectID? Create(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (!prefab)
                return null;
            
            if (!predictionManager.TryGetPrefab(prefab, out var prefabId))
            {
                PurrLogger.LogError($"Failed to find prefab '{prefab}' in the prediction manager");
                return default;
            }
            
            var id = new PredictedObjectID(_nextInstanceId);
            GameObject go;
            
            // check if we have a cached object
            if (TryTakeFromCache(id, out var cached))
            {
                cached.SetActive(true);
                cached.transform.SetPositionAndRotation(position, rotation);
                go = cached;
            }
            else
            {
                go = predictionManager.InternalCreate(prefab, position, rotation);
                go.GetComponentsInChildren<PredictedIdentity>(true, _tempList);

                foreach (var item in _tempList)
                    item._isFreshSpawn = false;
            }

            var details = new InstanceDetails(prefabId, id);
            _instanceMap.Add(id, go);
            _spawnedPrefabs.Add(details);
            _nextInstanceId++;
            
            return id;
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
            
            // cache the object
            PutInCache(id, instance);
        }

        private void PutInCache(PredictedObjectID id, GameObject instance)
        {
            instance.SetActive(false);
            _tempMap.Add(id, new CachedGameObject(Time.time, instance));
        }
        
        private bool TryTakeFromCache(PredictedObjectID id, out GameObject go)
        {
            if (_tempMap.Remove(id, out var cached))
            {
                go = cached.gameObject;
                return true;
            }
            
            go = null;
            return false;
        }

        public void Delete(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return;
            
            Delete(id.Value);
        }
    }
}
