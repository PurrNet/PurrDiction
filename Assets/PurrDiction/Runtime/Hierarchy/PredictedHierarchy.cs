using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<InstanceDetails> _spawnedPrefabs = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        readonly Dictionary<GameObject, PredictedObjectID> _goToId = new ();
        readonly HashSet<PredictedObjectID> _isSceneObject = new ();

        private uint _nextInstanceId = 2;

        protected override PredictedHierarchyState GetInitialState()
        {
            var state = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(16),
                DisposableList<PredictedObjectID>.Create(16),
                _nextInstanceId);
            return state;
        }

        protected override void GetUnityState(ref PredictedHierarchyState state)
        {
            int count = _spawnedPrefabs.Count;
            state.spawnedPrefabs.Clear();

            if (state.spawnedPrefabs.list.Capacity < count)
                state.spawnedPrefabs.list.Capacity = count;

            for (var i = 0; i < count; i++)
                state.spawnedPrefabs.Add(_spawnedPrefabs[i]);

            state.nextInstanceId = _nextInstanceId;
        }

        void Apply(List<InstanceDetails> list, DisposableList<DiffOp<InstanceDetails>> ops)
        {
            int offset = 0;
            int count = ops.Count;
            for (var i = 0; i < count; i++)
            {
                var op = ops[i];
                switch (op.type)
                {
                    case OperationType.Add:
                        for (var j = 0; j < op.values.Count; j++)
                        {
                            var details = op.values[j];
                            var pid = details.prefabId;
                            var instanceId = details.instanceId;
                            var goId = CreateInsertedWithID(instanceId.instanceId.value, _spawnedPrefabs.Count, pid, details.spawnPosition, details.spawnRotation, details.owner);
                            if (!goId.HasValue)
                                PurrLogger.LogError($"Mismatch: Failed to create prefab {pid}");
                        }
                        offset += op.values.Count;
                        op.values.Dispose();
                        break;
                    case OperationType.Insert:
                    {
                        int start = op.index + offset;
                        for (var j = 0; j < op.values.Count; j++)
                        {
                            int insertIndex = start + j;

                            var details = op.values[j];
                            var pid = details.prefabId;
                            var instanceId = details.instanceId;
                            var goId = CreateInsertedWithID(instanceId.instanceId.value, insertIndex, pid, details.spawnPosition, details.spawnRotation,
                                details.owner);
                            if (!goId.HasValue)
                                PurrLogger.LogError($"Mismatch: Failed to create prefab {pid}");
                        }
                        offset += op.values.Count;
                        op.values.Dispose();
                        break;
                    }
                    case OperationType.Delete:
                    {
                        int start = op.index + offset;
                        for (int j = start; j < start + op.length; j++)
                        {
                            var details = list[j];
                            if (_instanceMap.Remove(details.instanceId, out var instance) && instance)
                            {
                                _goToId.Remove(instance);
                                Delete(details, instance, true, false);
                            }
                            else
                            {
                                PurrLogger.LogError($"Mismatch: Failed to delete prefab instance {details.instanceId}");
                            }
                        }

                        list.RemoveRange(start, op.length);
                        offset -= op.length;
                        break;
                    }
                    case OperationType.End:
                    default:
                        break;
                }
            }
        }

        private bool _isRollingBack = false;

        protected override void SetUnityState(PredictedHierarchyState state)
        {
            _isRollingBack = true;
            var actions = MyersDiff.Diff(_spawnedPrefabs, state.spawnedPrefabs);

            Apply(_spawnedPrefabs, actions);

            _nextInstanceId = state.nextInstanceId;
            _isRollingBack = false;
        }

        public PredictedObjectID? Create(int prefabId, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;

            return Create(prefab, owner);
        }

        public PredictedObjectID? Create(GameObject prefab, Vector3 position, Quaternion rotation, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, position, rotation, owner);
        }

        PredictedObjectID? CreateInsertedWithID(uint iid, int index, int prefabId, Vector3 position, Quaternion rotation,
            PlayerID? owner = null)
        {
            var instanceId = new PredictedObjectID(iid);
            var key = new InstanceDetails(prefabId, instanceId, position, rotation, owner);

            GameObject go;

            var pool = GetPool(prefabId);

            if (pool.TryTakePrecise(key, out var instance))
            {
                go = instance;
                go.transform.SetPositionAndRotation(position, rotation);
                predictionManager.RegisterInstance(go, instanceId, owner, false, false);
                go.SetActive(true);
            }
            else if (key.prefabId.value < 0 && pool.TryTakeSceneObject(key, out var sceneObj))
            {
                go = sceneObj;
                go.transform.SetPositionAndRotation(position, rotation);
                predictionManager.RegisterInstance(go, instanceId, owner, false, false);
                go.SetActive(true);
            }
            else
            {
                if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                {
                    PurrLogger.LogError($"Failed to get prefab {prefabId}");
                    return default;
                }

                go = predictionManager.InternalCreate(prefab, position, rotation, instanceId, owner);
            }

            if (_instanceMap.Remove(instanceId, out var other))
                PurrLogger.LogError($"Duplicate instance ID {instanceId} for prefab {prefabId}. Existing GameObject: `{other.name}`, New GameObject: `{go.name}`", other);

            _instanceMap[instanceId] = go;
            _goToId[go] = instanceId;
            _spawnedPrefabs.Insert(index, key);

            if (!_isRollingBack && !predictionManager.isSimulating)
            {
                ref var state = ref currentState;
                GetUnityState(ref state);
            }

            return instanceId;
        }

        public PredictedObjectID? Create(int prefabId, Vector3 position, Quaternion rotation, PlayerID? owner = null)
        {
            return CreateInsertedWithID(_nextInstanceId++, _spawnedPrefabs.Count, prefabId, position, rotation, owner);
        }

        readonly Dictionary<int, PredictedTickPool> _prefabToPool = new ();

        private PredictedTickPool GetPool(int prefabId)
        {
            if (_prefabToPool.TryGetValue(prefabId, out var pool))
                return pool;

            pool = new PredictedTickPool(new List<PooledInstance>(10));
            _prefabToPool.Add(prefabId, pool);
            return pool;
        }

        protected override void Simulate(ref PredictedHierarchyState state, float delta)
        {
            for (var o = 0; o < state.toDelete.Count; o++)
                DeleteNow(state.toDelete[o]);
            state.toDelete.Clear();
        }

        private void LateUpdate()
        {
            foreach (var (pid, pool) in _prefabToPool)
            {
                if (pid < 0)
                    return;

                pool.ClearOld(predictionManager);
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ClearPool();
        }

        private void ClearPool()
        {
            foreach (var (pid, pool) in _prefabToPool)
            {
                if (pid < 0)
                    return;

                pool.Clear(predictionManager);
            }
        }

        private void Delete(InstanceDetails details, GameObject go, bool canPool, bool triggerDestroyEvent)
        {
            if (!canPool)
            {
                predictionManager.InternalDelete(details.prefabId, go);
                return;
            }

            var pool = GetPool(details.prefabId);

            if (pool.Put(details, go, predictionManager.localTick))
            {
                go.SetActive(false);
                predictionManager.UnregisterInstance(go, false, triggerDestroyEvent);
            }
            else
            {
                predictionManager.InternalDelete(details.prefabId, go);
            }
        }

        internal void RegisterSceneObject(GameObject root, int pid)
        {
            var instanceId = new PredictedObjectID(_nextInstanceId++);
            var key = new InstanceDetails(pid, instanceId, root.transform.position, root.transform.rotation, null);

            _isSceneObject.Add(instanceId);
            _instanceMap.Add(instanceId, root);
            _goToId.Add(root, instanceId);
            _spawnedPrefabs.Add(key);

            predictionManager.RegisterInstance(root, instanceId, null, false, false);
        }

        public PredictedObjectID? Create(GameObject prefab, PlayerID? owner = null)
        {
            var trs = prefab.transform;
            trs.GetPositionAndRotation(out var position, out var rotation);

            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, position, rotation, owner);
        }

        public bool TryCreate(int prefabId, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefabId, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreate(GameObject prefab, Vector3 position, Quaternion rotation, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefab, position, rotation, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreate(GameObject prefab, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefab, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreateAndGet<T>(int prefabId, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefabId, owner);
            return TryGetComponent(objId, out component);
        }

        public bool TryCreateAndGet<T>(GameObject prefab, Vector3 position, Quaternion rotation, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefab, position, rotation, owner);
            return TryGetComponent(objId, out component);
        }

        public bool TryCreateAndGet<T>(GameObject prefab, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefab, owner);
            return TryGetComponent(objId, out component);
        }

        public GameObject GetGameObject(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return null;

            return _instanceMap.GetValueOrDefault(id.Value);
        }

        public T GetComponent<T>(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return default;

            return GetComponent<T>(id.Value);
        }

        public T GetComponent<T>(PredictedObjectID id)
        {
            var go = _instanceMap.GetValueOrDefault(id);
            if (!go) return default;
            return go.GetComponent<T>();
        }

        public bool TryGetComponent<T>(PredictedObjectID id, out T go)
        {
            go = GetComponent<T>(id);
            return go != null;
        }

        public bool TryGetComponent<T>(PredictedObjectID? id, out T go)
        {
            go = GetComponent<T>(id);
            return go != null;
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

        private void DeleteNow(PredictedObjectID id)
        {
            if (!_instanceMap.Remove(id, out var instance))
                return;

            var isVerified = predictionManager.isVerified;
            _goToId.Remove(instance);

            var count = _spawnedPrefabs.Count;
            for (var i = 0; i < count; i++)
            {
                var details = _spawnedPrefabs[i];
                if (details.instanceId.Equals(id))
                {
                    _spawnedPrefabs.RemoveAt(i);
                    var prefabId = details.prefabId.value;
                    Delete(details, instance, prefabId < 0 || !isVerified, true);
                    return;
                }
            }

            throw new KeyNotFoundException($"PredictedObjectID {id} not found in spawned prefabs.");
        }

        public void Delete(GameObject go)
        {
            if (!go)
                return;

            if (go.TryGetComponent<PredictedGameObject>(out var pgo))
            {
                pgo.SetActive(false);
                return;
            }

            // look for all nested objects and delete them as well
            ReccursiveDel(go.transform);
        }

        private void ReccursiveDel(Transform trs)
        {
            int children = trs.childCount;

            for (int i = 0; i < children; i++)
            {
                var child = trs.GetChild(i);
                ReccursiveDel(child);
            }

            if (_goToId.TryGetValue(trs.gameObject, out var poid))
                currentState.toDelete.Add(poid);
        }

        public void Delete(PredictedIdentity pid)
        {
            if (pid)
                Delete(pid.gameObject);
        }

        public void Delete(PredictedObjectID? id)
        {
            if (id.TryGetGameObject(predictionManager, out var go))
                Delete(go);
        }

        public void Cleanup()
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var instance = _spawnedPrefabs[i];
                if (!_instanceMap.TryGetValue(instance.instanceId, out var go))
                    continue;

                if (_isSceneObject.Contains(instance.instanceId))
                {
                    predictionManager.UnregisterInstance(go, true, true);
                    continue;
                }

                predictionManager.InternalDelete(instance.prefabId, go);
            }

            _instanceMap.Clear();
            _goToId.Clear();
            _spawnedPrefabs.Clear();
            _isSceneObject.Clear();
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
