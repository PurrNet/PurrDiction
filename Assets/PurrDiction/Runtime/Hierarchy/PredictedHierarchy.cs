using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<InstanceDetails> _spawnedPrefabs = new ();
        readonly Dictionary<PredictedObjectID, InstanceDetails> _recordsById = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        readonly Dictionary<GameObject, PredictedObjectID> _goToId = new ();
        readonly HashSet<PredictedObjectID> _isSceneObject = new ();
        readonly List<PredictedObjectID> _reservedSceneObjects = new ();
        readonly Dictionary<int, PiecePrototype> _prototypes = new ();
        readonly PredictedPiecePool _pool = new ();

        readonly Dictionary<PredictedObjectID, int> _targetIdsScratch = new ();
        readonly List<InstanceDetails> _removalScratch = new ();
        readonly HashSet<PredictedObjectID> _removalSetScratch = new ();
        readonly Dictionary<PredictedObjectID, List<InstanceDetails>> _additionGroups = new ();
        readonly List<PredictedObjectID> _additionGroupOrder = new ();
        readonly Dictionary<uint, GameObject> _pieceGoScratch = new ();
        readonly List<PooledPiece> _takenPiecesScratch = new ();
        readonly HashSet<uint> _individuallyTakenScratch = new ();
        readonly List<InstanceDetails> _donorNeededScratch = new ();
        readonly List<PooledPiece> _memberPiecesScratch = new ();
        readonly List<InstanceDetails> _recordBuildScratch = new ();
        readonly List<GameObject> _collectScratch = new ();
        readonly HashSet<uint> _wantedPieceScratch = new ();
        readonly HashSet<uint> _donorNeededIndexScratch = new ();
        readonly HashSet<PredictedObjectID> _removedRecordScratch = new ();
        readonly Dictionary<PredictedObjectID, PredictedObjectID> _cascadeParentScratch = new ();
        readonly Dictionary<PredictedObjectID, bool> _cascadeReachScratch = new ();
        readonly List<InstanceDetails> _localInterestRecordsScratch = new ();
        readonly HashSet<PredictedObjectID> _localInterestMembersScratch = new ();

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

        private bool _isRollingBack = false;

        protected override void SetUnityState(PredictedHierarchyState state)
        {
            _isRollingBack = true;

            var target = state.spawnedPrefabs;

            _targetIdsScratch.Clear();
            for (var i = 0; i < target.Count; i++)
                _targetIdsScratch[target[i].instanceId] = i;

            _removalScratch.Clear();
            _removalSetScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (_targetIdsScratch.TryGetValue(record.instanceId, out var targetIndex))
                {
                    var targetRecord = target[targetIndex];
                    if (targetRecord.prefabId == record.prefabId && targetRecord.pieceIndex.value == record.pieceIndex.value &&
                        System.Nullable.Equals(targetRecord.parent, record.parent))
                        continue;
                }

                _removalScratch.Add(record);
                _removalSetScratch.Add(record.instanceId);
            }

            if (_removalScratch.Count > 0)
                RemovePieceSet(_removalScratch, _removalSetScratch, true, false, false);

            _spawnedPrefabs.Clear();
            _recordsById.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];
                _spawnedPrefabs.Add(record);
                _recordsById[record.instanceId] = record;
            }

            _additionGroups.Clear();
            _additionGroupOrder.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];

                if (_instanceMap.ContainsKey(record.instanceId))
                    continue;

                var rootId = record.rootId;
                if (!predictionManager.ShouldMaterializeLocalRoot(rootId))
                    continue;

                if (!_additionGroups.TryGetValue(rootId, out var list))
                {
                    list = ListPool<InstanceDetails>.Instantiate();
                    _additionGroups[rootId] = list;
                    _additionGroupOrder.Add(rootId);
                }

                list.Add(record);
            }

            for (var g = 0; g < _additionGroupOrder.Count; g++)
            {
                var rootId = _additionGroupOrder[g];
                var records = _additionGroups[rootId];
                records.Sort((a, b) => a.pieceIndex.value.CompareTo(b.pieceIndex.value));

                var prefabId = records[0].prefabId;
                var proto = GetPrototype(prefabId);

                if (proto == null)
                {
                    PurrLogger.LogError($"Mismatch: no prototype for prefab {prefabId}; cannot recreate instance {rootId}.");
                    ListPool<InstanceDetails>.Destroy(records);
                    continue;
                }

                bool liveAny = false;
                for (var k = 0; k < proto.pieceCount && !liveAny; k++)
                    liveAny = _instanceMap.ContainsKey(new PredictedObjectID(rootId.instanceId.value + (uint)k));

                if (!liveAny)
                {
                    CreateWholeInstance(prefabId, proto, records);
                }
                else
                {
                    for (var r = 0; r < records.Count; r++)
                        ResurrectPiece(records[r], proto);
                }

                ListPool<InstanceDetails>.Destroy(records);
            }

            _nextInstanceId = state.nextInstanceId;

            _isRollingBack = false;
        }

        internal static bool SameAttach(PredictedComponentID? a, PredictedComponentID? b)
        {
            if (a.HasValue != b.HasValue)
                return false;
            return !a.HasValue || a.Value.objectId.Equals(b.Value.objectId);
        }

        private PredictedComponentID? GetDefaultParent(in InstanceDetails record)
        {
            if (record.pieceIndex.value > 0)
            {
                var proto = GetPrototype(record.prefabId);

                if (proto == null)
                    return null;

                int parentPieceIndex = proto.pieces[record.pieceIndex.value].parentPieceIndex;
                var parentId = new PredictedObjectID(record.rootId.instanceId.value + (uint)parentPieceIndex);
                return new PredictedComponentID(parentId, 0);
            }

            if (record.parent.HasValue)
                return new PredictedComponentID(record.parent.Value.objectId, 0);

            return null;
        }

        internal PiecePrototype GetPrototype(int prefabId)
        {
            if (_prototypes.TryGetValue(prefabId, out var proto))
                return proto;

            if (prefabId < 0)
                return null;

            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return null;

            proto = PiecePrototype.Build(prefab);
            _prototypes[prefabId] = proto;
            return proto;
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

        public PredictedObjectID? Create(int prefabId, Vector3 position, Quaternion rotation, PlayerID? owner = null)
        {
            return CreateInstance(prefabId, position, rotation, owner, null);
        }

        /// <summary>
        /// Spawns a prefab parented under the transform of the given predicted component.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? Create(int prefabId, Vector3 localPosition, Quaternion localRotation, PredictedComponentID parent, PlayerID? owner = null)
        {
            return CreateInstance(prefabId, localPosition, localRotation, owner, parent);
        }

        /// <summary>
        /// Spawns a prefab parented under the transform of the given predicted component.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? Create(GameObject prefab, Vector3 localPosition, Quaternion localRotation, PredictedComponentID parent, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, localPosition, localRotation, parent, owner);
        }

        /// <summary>
        /// Spawns a prefab parented under the given predicted identity.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? Create(GameObject prefab, Vector3 localPosition, Quaternion localRotation, PredictedIdentity parent, PlayerID? owner = null)
        {
            if (!parent)
                return Create(prefab, localPosition, localRotation, owner);

            return Create(prefab, localPosition, localRotation, parent.id, owner);
        }

        /// <summary>
        /// Spawns a prefab parented under the predicted object the given transform belongs to.
        /// The transform must be on a GameObject carrying a PredictedIdentity; attaching at
        /// plain nested transforms is not supported and logs an error.
        /// </summary>
        public PredictedObjectID? Create(GameObject prefab, Vector3 localPosition, Quaternion localRotation, Transform parent, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, localPosition, localRotation, parent, owner);
        }

        /// <summary>
        /// Spawns a prefab parented under the predicted object the given transform belongs to.
        /// The transform must be on a GameObject carrying a PredictedIdentity; attaching at
        /// plain nested transforms is not supported and logs an error.
        /// </summary>
        public PredictedObjectID? Create(int prefabId, Vector3 localPosition, Quaternion localRotation, Transform parent, PlayerID? owner = null)
        {
            if (!parent)
                return Create(prefabId, localPosition, localRotation, owner);

            if (!TryGetId(parent.gameObject, out var parentId))
            {
                PurrLogger.LogError(
                    $"'{parent.name}' is not a predicted object. Parent transforms must be on a GameObject with a PredictedIdentity; attaching at plain nested transforms is not supported.", parent);
                return default;
            }

            return Create(prefabId, localPosition, localRotation, new PredictedComponentID(parentId, 0), owner);
        }

        private PredictedObjectID? CreateInstance(int prefabId, Vector3 position, Quaternion rotation, PlayerID? owner, PredictedComponentID? parent)
        {
            var proto = GetPrototype(prefabId);

            if (proto == null)
            {
                PurrLogger.LogError($"Failed to get prefab {prefabId}");
                return default;
            }

            uint baseId = _nextInstanceId;
            _nextInstanceId += (uint)proto.pieceCount;

            _recordBuildScratch.Clear();
            var rootId = new PredictedObjectID(baseId);
            _recordBuildScratch.Add(new InstanceDetails(prefabId, 0, rootId, position, rotation, owner, parent));

            for (var k = 1; k < proto.pieceCount; k++)
                _recordBuildScratch.Add(new InstanceDetails(prefabId, (uint)k, new PredictedObjectID(baseId + (uint)k), Vector3.zero, Quaternion.identity, null, null));

            var rootGo = CreateWholeInstance(prefabId, proto, _recordBuildScratch);

            if (!rootGo)
                return default;

            for (var k = 0; k < _recordBuildScratch.Count; k++)
            {
                var record = _recordBuildScratch[k];
                _spawnedPrefabs.Add(record);
                _recordsById[record.instanceId] = record;
            }

            NotifyInstanceParentChanged(rootGo);

            if (!_isRollingBack && !predictionManager.isSimulating)
            {
                ref var state = ref currentState;
                GetUnityState(ref state);
            }

            return rootId;
        }

        private GameObject CreateWholeInstance(int prefabId, PiecePrototype proto, List<InstanceDetails> records)
        {
            _pieceGoScratch.Clear();
            _takenPiecesScratch.Clear();
            _individuallyTakenScratch.Clear();
            _donorNeededScratch.Clear();

            bool hasRoot = records[0].isRootRecord;
            var rootRecord = records[0];
            var rootId = rootRecord.rootId;

            bool reset = false;
            bool removedFromPoolEvent = false;
            GameObject rootGo = null;

            Transform parentTrs = null;

            if (hasRoot && rootRecord.parent.HasValue)
            {
                if (predictionManager.TryGetIdentity(rootRecord.parent.Value, out var parentIdentity) && parentIdentity)
                    parentTrs = parentIdentity.transform;
                else if (!IsUnavailableDueToLocalInterest(rootRecord.parent.Value))
                    PurrLogger.LogError($"Failed to resolve spawn parent {rootRecord.parent.Value} for prefab {prefabId}; spawning unparented.");
            }

            if (hasRoot)
            {
                if (!_pool.TryTakeTree(rootRecord.instanceId, prefabId, rootRecord.spawnPosition, prefabId >= 0, _takenPiecesScratch, out rootGo, out var drifted))
                {
                    if (drifted || prefabId < 0)
                        _pool.TryTakeNearestCompleteTree(prefabId, rootRecord.spawnPosition, _takenPiecesScratch, out rootGo);
                }

                if (rootGo)
                {
                    if (!PreservesSoftCorrectionRootPose(rootGo, rootRecord.instanceId))
                        ApplySpawnPose(rootGo.transform, parentTrs, rootRecord.spawnPosition, rootRecord.spawnRotation);
                    else if (parentTrs)
                        rootGo.transform.SetParent(parentTrs, true);

                    for (var i = 0; i < _takenPiecesScratch.Count; i++)
                        _pieceGoScratch[_takenPiecesScratch[i].pieceIndex] = _takenPiecesScratch[i].gameObject;
                }
                else
                {
                    if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                    {
                        PurrLogger.LogError($"Failed to get prefab {prefabId}");
                        return null;
                    }

                    var worldPosition = parentTrs ? parentTrs.TransformPoint(rootRecord.spawnPosition) : rootRecord.spawnPosition;
                    var worldRotation = parentTrs ? parentTrs.rotation * rootRecord.spawnRotation : rootRecord.spawnRotation;

                    rootGo = predictionManager.InternalCreate(prefab, worldPosition, worldRotation, out var fromPool);
                    reset = fromPool;
                    removedFromPoolEvent = fromPool;

                    if (parentTrs)
                        ApplySpawnPose(rootGo.transform, parentTrs, rootRecord.spawnPosition, rootRecord.spawnRotation);

                    if (!proto.TryCollectInstancePieces(rootGo, _collectScratch))
                    {
                        UnityProxy.DestroyImmediateDirectly(rootGo);
                        return null;
                    }

                    for (var k = 0; k < _collectScratch.Count; k++)
                        _pieceGoScratch[(uint)k] = _collectScratch[k];
                }
            }

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];
                uint k = record.pieceIndex.value;

                if (_pieceGoScratch.ContainsKey(k))
                    continue;

                if (_pool.TryTakePiece(record.instanceId, prefabId, out var pieceGo))
                {
                    _pieceGoScratch[k] = pieceGo;
                    _individuallyTakenScratch.Add(k);
                }
                else
                {
                    _donorNeededScratch.Add(record);
                }
            }

            if (_donorNeededScratch.Count > 0)
                ExtractFromDonor(prefabId, proto, _donorNeededScratch, _pieceGoScratch, _individuallyTakenScratch);

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];
                uint k = record.pieceIndex.value;

                if (!_individuallyTakenScratch.Contains(k) || !_pieceGoScratch.TryGetValue(k, out var pieceGo) || !pieceGo)
                    continue;

                var pp = proto.pieces[k];
                Transform attach = null;

                if (pp.parentPieceIndex >= 0 && _pieceGoScratch.TryGetValue((uint)pp.parentPieceIndex, out var parentGo) && parentGo)
                    attach = parentGo.transform;
                else if (rootGo)
                    attach = rootGo.transform;

                if (attach)
                {
                    PiecePrototype.AttachAtPath(attach, pieceGo.transform, pp.inverseSiblingPath, false);
                    pieceGo.transform.localPosition = pp.localPosition;
                    pieceGo.transform.localRotation = pp.localRotation;
                    pieceGo.transform.localScale = pp.localScale;
                }
                else
                {
                    pieceGo.transform.SetParent(null, false);
                    pieceGo.transform.SetPositionAndRotation(record.spawnPosition, record.spawnRotation);
                }

                pieceGo.SetActive(pp.activeSelf);
            }

            _wantedPieceScratch.Clear();
            for (var r = 0; r < records.Count; r++)
                _wantedPieceScratch.Add(records[r].pieceIndex.value);

            for (var k = proto.pieceCount - 1; k >= 0; k--)
            {
                uint pieceIndex = (uint)k;

                if (_wantedPieceScratch.Contains(pieceIndex))
                    continue;

                if (!_pieceGoScratch.TryGetValue(pieceIndex, out var extraGo) || !extraGo)
                    continue;

                var extraTrs = extraGo.transform;

                for (var r = 0; r < records.Count; r++)
                {
                    if (_pieceGoScratch.TryGetValue(records[r].pieceIndex.value, out var wantedGo) && wantedGo &&
                        wantedGo != extraGo && wantedGo.transform.IsChildOf(extraTrs))
                    {
                        wantedGo.transform.SetParent(null, true);
                    }
                }

                var extraId = new PredictedObjectID(rootId.instanceId.value + pieceIndex);
                extraTrs.SetParent(null, true);
                extraGo.SetActive(false);
                _pool.PutPiece(prefabId, extraId, pieceIndex, extraGo, predictionManager.localTick);
                _pieceGoScratch.Remove(pieceIndex);
            }

            var instanceOwner = rootRecord.owner;

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];

                if (!_pieceGoScratch.TryGetValue(record.pieceIndex.value, out var pieceGo) || !pieceGo)
                {
                    PurrLogger.LogError($"Mismatch: failed to materialize piece {record.instanceId} of prefab {prefabId}.");
                    continue;
                }

                if (_instanceMap.Remove(record.instanceId, out var other))
                    PurrLogger.LogError($"Duplicate instance ID {record.instanceId} for prefab {prefabId}. Existing GameObject: `{other.name}`, New GameObject: `{pieceGo.name}`", other);

                _instanceMap[record.instanceId] = pieceGo;
                _goToId[pieceGo] = record.instanceId;

                predictionManager.RegisterInstance(pieceGo, record.instanceId, instanceOwner, reset, removedFromPoolEvent);
            }

            if (rootGo && !rootGo.activeSelf)
                rootGo.SetActive(true);

            if (!rootGo && records.Count > 0 && _pieceGoScratch.TryGetValue(records[0].pieceIndex.value, out var firstGo))
                return firstGo;

            return rootGo;
        }

        private void ExtractFromDonor(int prefabId, PiecePrototype proto, List<InstanceDetails> needed,
            Dictionary<uint, GameObject> pieceGos, HashSet<uint> individuallyTaken)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
            {
                PurrLogger.LogError($"Cannot rebuild {needed.Count} piece(s) of prefab {prefabId}: no prefab asset to instantiate (scene pieces cannot be rebuilt once destroyed).");
                return;
            }

            var donor = UnityProxy.InstantiateDirectly(prefab, Vector3.zero, Quaternion.identity, gameObject.scene);

            if (!proto.TryCollectInstancePieces(donor, _collectScratch))
            {
                UnityProxy.DestroyImmediateDirectly(donor);
                return;
            }

            var donorPieces = ListPool<GameObject>.Instantiate();
            donorPieces.AddRange(_collectScratch);

            for (var k = donorPieces.Count - 1; k >= 1; k--)
                donorPieces[k].transform.SetParent(null, false);

            _donorNeededIndexScratch.Clear();
            for (var n = 0; n < needed.Count; n++)
                _donorNeededIndexScratch.Add(needed[n].pieceIndex.value);

            for (var k = 0; k < donorPieces.Count; k++)
            {
                bool isNeeded = _donorNeededIndexScratch.Contains((uint)k);

                if (isNeeded)
                {
                    pieceGos[(uint)k] = donorPieces[k];
                    individuallyTaken.Add((uint)k);
                }
                else
                {
                    UnityProxy.DestroyImmediateDirectly(donorPieces[k]);
                }
            }

            ListPool<GameObject>.Destroy(donorPieces);
        }

        private void ResurrectPiece(InstanceDetails record, PiecePrototype proto)
        {
            uint k = record.pieceIndex.value;

            if (k >= proto.pieceCount)
            {
                PurrLogger.LogError($"Mismatch: piece index {k} out of range for prefab {record.prefabId}.");
                return;
            }

            if (!_pool.TryTakePiece(record.instanceId, record.prefabId, out var pieceGo))
            {
                _donorNeededScratch.Clear();
                _donorNeededScratch.Add(record);
                _pieceGoScratch.Clear();
                _individuallyTakenScratch.Clear();
                ExtractFromDonor(record.prefabId, proto, _donorNeededScratch, _pieceGoScratch, _individuallyTakenScratch);
                _pieceGoScratch.TryGetValue(k, out pieceGo);
            }

            if (!pieceGo)
            {
                PurrLogger.LogError($"Mismatch: failed to resurrect piece {record.instanceId} of prefab {record.prefabId}.");
                return;
            }

            var pp = proto.pieces[k];
            var defaultParentId = new PredictedObjectID(record.rootId.instanceId.value + (uint)pp.parentPieceIndex);

            if (pp.parentPieceIndex >= 0 && _instanceMap.TryGetValue(defaultParentId, out var parentGo) && parentGo)
            {
                PiecePrototype.AttachAtPath(parentGo.transform, pieceGo.transform, pp.inverseSiblingPath, false);
                pieceGo.transform.localPosition = pp.localPosition;
                pieceGo.transform.localRotation = pp.localRotation;
                pieceGo.transform.localScale = pp.localScale;
            }
            else
            {
                pieceGo.transform.SetParent(null, false);
                pieceGo.transform.SetPositionAndRotation(record.spawnPosition, record.spawnRotation);
            }

            pieceGo.SetActive(pp.activeSelf);

            var recordOwner = record.owner;
            if (_recordsById.TryGetValue(record.rootId, out var rootRecord))
                recordOwner = rootRecord.owner;

            if (_instanceMap.Remove(record.instanceId, out var other))
                PurrLogger.LogError($"Duplicate instance ID {record.instanceId}. Existing GameObject: `{other.name}`, New GameObject: `{pieceGo.name}`", other);

            _instanceMap[record.instanceId] = pieceGo;
            _goToId[pieceGo] = record.instanceId;

            predictionManager.RegisterInstance(pieceGo, record.instanceId, recordOwner, false, false);
        }

        private static void ApplySpawnPose(Transform trs, Transform parent, Vector3 position, Quaternion rotation)
        {
            if (parent)
            {
                trs.SetParent(parent, false);
                trs.localPosition = position;
                trs.localRotation = rotation;
            }
            else
            {
                trs.SetPositionAndRotation(position, rotation);
            }
        }

        private bool _suppressParentWarnings;

        readonly Dictionary<GameObject, (int frame, Transform parent)> _policyRefreshStamp = new ();
        readonly HashSet<GameObject> _parentingWarned = new ();

        internal void NotifyInstanceParentChanged(GameObject go)
        {
            if (!_goToId.TryGetValue(go, out var instanceId))
                return;

            int frame = Time.frameCount;
            var currentParent = go.transform.parent;

            if (!_policyRefreshStamp.TryGetValue(go, out var stamp) || stamp.frame != frame || stamp.parent != currentParent)
            {
                _policyRefreshStamp[go] = (frame, currentParent);
                RefreshDescendantPolicies(go);
            }

            if (_isRollingBack || _suppressParentWarnings || predictionManager.isReplaying)
                return;

            if (!_recordsById.TryGetValue(instanceId, out var record))
                return;

            PredictedComponentID? resolved = TryResolveParentLink(go, out var parentLink)
                ? parentLink
                : null;

            if (SameAttach(resolved, GetDefaultParent(record)))
                return;

            if (!_parentingWarned.Add(go))
                return;

            if (!go.TryGetComponent<PredictedParent>(out _))
                PurrLogger.LogWarning($"'{go.name}' was reparented but has no PredictedParent component; the change is local-only and the simulation still treats it as attached to its default parent.", go);

            if (resolved.HasValue)
                ValidateRuntimeParenting(go);
        }

        private bool _isCleaningUp;

        internal void NotifyPieceDestroyed(GameObject go)
        {
            if (_isCleaningUp || !_goToId.Remove(go, out var instanceId))
                return;

            _instanceMap.Remove(instanceId);
            _isSceneObject.Remove(instanceId);
            RemoveRecord(instanceId);

            PurrLogger.LogWarning($"'{go.name}' ({instanceId}) was destroyed directly; treating it as a hard delete. Use hierarchy.Delete so the piece can be pooled and resurrected by rollbacks.");

            if (!_isRollingBack && predictionManager && !predictionManager.isSimulating)
            {
                ref var state = ref currentState;
                GetUnityState(ref state);
            }
        }

        internal void CollectInstanceIdentities(GameObject root, PredictedObjectID anyPieceId, List<PredictedIdentity> result)
        {
            var instanceRoot = TryGetRootId(anyPieceId, out var resolvedRoot) ? resolvedRoot : anyPieceId;
            CollectInstanceIdentitiesRecursive(root.transform, instanceRoot, result);
        }

        private void CollectInstanceIdentitiesRecursive(Transform current, PredictedObjectID instanceRoot, List<PredictedIdentity> result)
        {
            if (_goToId.TryGetValue(current.gameObject, out var pieceId) &&
                _recordsById.TryGetValue(pieceId, out var record) && !record.rootId.Equals(instanceRoot))
            {
                return;
            }

            var identities = ListPool<PredictedIdentity>.Instantiate();
            current.GetComponents(identities);
            result.AddRange(identities);
            ListPool<PredictedIdentity>.Destroy(identities);

            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
                CollectInstanceIdentitiesRecursive(current.GetChild(i), instanceRoot, result);
        }

        internal bool TryRestoreAttach(PredictedObjectID pieceId, PredictedComponentID? target)
        {
            if (!_instanceMap.TryGetValue(pieceId, out var go) || !go)
                return false;

            if (!target.HasValue)
            {
                go.transform.SetParent(null, true);
                return true;
            }

            if (!predictionManager.TryGetIdentity(target.Value, out var parentIdentity) || !parentIdentity)
                return false;

            if (_recordsById.TryGetValue(pieceId, out var record) && SameAttach(GetDefaultParent(record), target))
            {
                var proto = GetPrototype(record.prefabId);
                var path = proto != null && record.pieceIndex.value > 0
                    ? proto.pieces[record.pieceIndex.value].inverseSiblingPath
                    : null;
                PiecePrototype.AttachAtPath(parentIdentity.transform, go.transform, path, true);
                return true;
            }

            go.transform.SetParent(parentIdentity.transform, true);
            return true;
        }

        private void ValidateRuntimeParenting(GameObject go)
        {
            if (go.TryGetComponent(out Rigidbody rb) && !rb.isKinematic)
                PurrLogger.LogWarning($"'{go.name}' has a non-kinematic Rigidbody and was parented under a predicted object; physics simulates in world space, so the attachment will fight the rigidbody. Make it kinematic while attached.", go);
            else if (go.TryGetComponent(out Rigidbody2D rb2d) && rb2d.bodyType == RigidbodyType2D.Dynamic)
                PurrLogger.LogWarning($"'{go.name}' has a dynamic Rigidbody2D and was parented under a predicted object; physics simulates in world space, so the attachment will fight the rigidbody. Make it kinematic while attached.", go);

            var parent = go.transform.parent;

            if (parent != null)
            {
                var scale = parent.lossyScale;
                if (Mathf.Abs(scale.x - 1f) > 0.001f || Mathf.Abs(scale.y - 1f) > 0.001f || Mathf.Abs(scale.z - 1f) > 0.001f)
                    PurrLogger.LogWarning($"'{go.name}' was parented under '{parent.name}' which has non-unit scale {scale}; predicted view interpolation ignores scale, expect visual drift.", parent);
            }
        }

        private void RefreshDescendantPolicies(GameObject go)
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, identities);

            for (var i = 0; i < identities.Count; i++)
                identities[i].RefreshResolvedPredictionPolicy();

            ListPool<PredictedIdentity>.Destroy(identities);
        }

        internal bool TryResolveParentLink(GameObject go, out PredictedComponentID parent)
        {
            var current = go.transform.parent;

            while (current != null)
            {
                if (current.TryGetComponent(out PredictedIdentity identity) &&
                    ReferenceEquals(identity.predictionManager, predictionManager))
                {
                    parent = new PredictedComponentID(identity.id.objectId, 0);
                    return true;
                }

                current = current.parent;
            }

            parent = default;
            return false;
        }

        private bool PreservesSoftCorrectionRootPose(GameObject instance, PredictedObjectID instanceId)
        {
            if (!predictionManager.isReplaying || !instance)
                return false;

            return instance.TryGetComponent(out PredictedTransform predictedTransform) &&
                   predictedTransform.id.objectId.Equals(instanceId) &&
                   predictedTransform.previousRegisteredPredictionPolicy == PredictionPolicy.SoftCorrection &&
                   predictedTransform.ResolvePredictionPolicyForSetup() == PredictionPolicy.SoftCorrection;
        }

        protected override void Simulate(ref PredictedHierarchyState state, float delta)
        {
            for (var o = 0; o < state.toDelete.Count; o++)
                DeleteNow(state.toDelete[o]);
            state.toDelete.Clear();
        }

        private void LateUpdate()
        {
            _pool.ClearOld(predictionManager);
        }

        protected override void OnDestroy()
        {
            _isCleaningUp = true;
            base.OnDestroy();
            ClearPool();
        }

        private void ClearPool()
        {
            _pool.Clear(predictionManager);
        }

        readonly List<(GameObject root, int pid)> _pendingSceneReservations = new ();
        readonly HashSet<Transform> _sceneBoundariesScratch = new ();

        internal void ReserveSceneObject(GameObject root, int pid)
        {
            _pendingSceneReservations.Add((root, pid));
        }

        internal void RegisterReservedSceneObjects()
        {
            _sceneBoundariesScratch.Clear();

            for (var i = 0; i < _pendingSceneReservations.Count; i++)
            {
                var root = _pendingSceneReservations[i].root;
                if (root)
                    _sceneBoundariesScratch.Add(root.transform);
            }

            for (var i = 0; i < _pendingSceneReservations.Count; i++)
            {
                var (root, pid) = _pendingSceneReservations[i];

                if (!root)
                    continue;

                var proto = PiecePrototype.Build(root, _sceneBoundariesScratch);

                if (proto == null)
                    continue;

                _prototypes[pid] = proto;

                if (!proto.TryCollectInstancePieces(root, _collectScratch, _sceneBoundariesScratch))
                    continue;

                uint baseId = _nextInstanceId;
                _nextInstanceId += (uint)proto.pieceCount;

                var rootId = new PredictedObjectID(baseId);
                root.transform.GetPositionAndRotation(out var rootPos, out var rootRot);

                PredictedComponentID? authoredParent = TryResolveParentLink(root, out var authoredLink)
                    ? authoredLink
                    : null;

                for (var k = 0; k < proto.pieceCount; k++)
                {
                    var pieceId = new PredictedObjectID(baseId + (uint)k);
                    var pieceGo = _collectScratch[k];

                    var record = k == 0
                        ? new InstanceDetails(pid, 0, pieceId, rootPos, rootRot, null, authoredParent)
                        : new InstanceDetails(pid, (uint)k, pieceId, Vector3.zero, Quaternion.identity, null, null);

                    _isSceneObject.Add(pieceId);
                    _instanceMap.Add(pieceId, pieceGo);
                    _goToId.Add(pieceGo, pieceId);
                    _spawnedPrefabs.Add(record);
                    _recordsById[pieceId] = record;

                    predictionManager.RegisterInstance(pieceGo, pieceId, null, false, false);
                }

                _reservedSceneObjects.Add(rootId);
            }

            for (var i = 0; i < _reservedSceneObjects.Count; i++)
            {
                var rootId = _reservedSceneObjects[i];
                if (_instanceMap.TryGetValue(rootId, out var root) && root)
                    NotifyInstanceParentChanged(root);
            }

            _reservedSceneObjects.Clear();
            _pendingSceneReservations.Clear();
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

        /// <summary>
        /// Resolves the root piece id of the spawn instance the given piece belongs to.
        /// </summary>
        public bool TryGetRootId(PredictedObjectID pieceId, out PredictedObjectID rootId)
        {
            if (_recordsById.TryGetValue(pieceId, out var record))
            {
                rootId = record.rootId;
                return true;
            }

            rootId = default;
            return false;
        }

        internal bool HasRootRecords(PredictedObjectID rootId)
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                if (_spawnedPrefabs[i].rootId.Equals(rootId))
                    return true;
            }

            return false;
        }

        internal bool MaterializeLocalRoot(PredictedObjectID rootId)
        {
            _localInterestRecordsScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                if (record.rootId.Equals(rootId))
                    _localInterestRecordsScratch.Add(record);
            }

            if (_localInterestRecordsScratch.Count == 0)
                return false;

            _localInterestRecordsScratch.Sort((a, b) => a.pieceIndex.value.CompareTo(b.pieceIndex.value));

            bool liveAny = false;
            bool missingAny = false;

            for (var i = 0; i < _localInterestRecordsScratch.Count; i++)
            {
                bool live = _instanceMap.TryGetValue(_localInterestRecordsScratch[i].instanceId, out var go) && go;
                liveAny |= live;
                missingAny |= !live;
            }

            if (!missingAny)
                return true;

            var prefabId = _localInterestRecordsScratch[0].prefabId;
            var proto = GetPrototype(prefabId);
            if (proto == null)
                return false;

            if (!liveAny)
                CreateWholeInstance(prefabId, proto, _localInterestRecordsScratch);
            else
            {
                for (var i = 0; i < _localInterestRecordsScratch.Count; i++)
                {
                    var record = _localInterestRecordsScratch[i];
                    if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                        ResurrectPiece(record, proto);
                }
            }

            for (var i = 0; i < _localInterestRecordsScratch.Count; i++)
            {
                if (!_instanceMap.TryGetValue(_localInterestRecordsScratch[i].instanceId, out var go) || !go)
                    return false;
            }

            return true;
        }

        internal void DematerializeLocalRoot(PredictedObjectID rootId)
        {
            _localInterestRecordsScratch.Clear();
            _localInterestMembersScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                if (!record.rootId.Equals(rootId))
                    continue;

                _localInterestRecordsScratch.Add(record);
                _localInterestMembersScratch.Add(record.instanceId);
            }

            if (_localInterestRecordsScratch.Count == 0)
                return;

            bool wasRollingBack = _isRollingBack;
            bool wasSuppressingParentWarnings = _suppressParentWarnings;
            _isRollingBack = true;
            _suppressParentWarnings = true;

            try
            {
                RemovePieceSet(_localInterestRecordsScratch, _localInterestMembersScratch, true, false, false);
            }
            finally
            {
                _suppressParentWarnings = wasSuppressingParentWarnings;
                _isRollingBack = wasRollingBack;
            }
        }

        internal bool IsUnavailableDueToLocalInterest(PredictedComponentID target)
        {
            return TryGetRootId(target.objectId, out var rootId) &&
                   !predictionManager.ShouldMaterializeLocalRoot(rootId) &&
                   (!_instanceMap.TryGetValue(target.objectId, out var go) || !go);
        }

        /// <summary>
        /// True when both pieces belong to the same spawn instance. Replaces
        /// id.objectId equality checks from before pieces had their own ids.
        /// </summary>
        public bool SameInstance(PredictedObjectID a, PredictedObjectID b)
        {
            return _recordsById.TryGetValue(a, out var recordA) &&
                   _recordsById.TryGetValue(b, out var recordB) &&
                   recordA.rootId.Equals(recordB.rootId);
        }

        /// <summary>
        /// True when both components belong to the same spawn instance.
        /// </summary>
        public bool SameInstance(PredictedIdentity a, PredictedIdentity b)
        {
            return a && b && SameInstance(a.id.objectId, b.id.objectId);
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
            if (!_recordsById.TryGetValue(id, out var record))
                return;

            if (!_instanceMap.TryGetValue(id, out var instance) || !instance)
            {
                _removalScratch.Clear();
                _removalSetScratch.Clear();
                CollectCascade(record);
                RemovePieceSet(_removalScratch, _removalSetScratch, true, true, true);
                if (record.isRootRecord)
                    PromoteOrphans(record);
                return;
            }

            _removalScratch.Clear();
            _removalSetScratch.Clear();
            CollectCascade(record);

            var isVerified = predictionManager.isVerified;
            bool canPool = record.prefabId.value < 0 || !isVerified;

            _suppressParentWarnings = true;
            try
            {
                RemovePieceSet(_removalScratch, _removalSetScratch, canPool, true, true);
            }
            finally
            {
                _suppressParentWarnings = false;
            }

            if (record.isRootRecord)
                PromoteOrphans(record);
        }

        private void CollectCascade(in InstanceDetails target)
        {
            var rootId = target.rootId;

            _cascadeParentScratch.Clear();
            _cascadeReachScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (!record.rootId.Equals(rootId))
                    continue;

                var parentObj = EffectiveParentObject(record);

                if (parentObj.HasValue && _recordsById.TryGetValue(parentObj.Value, out var parentRecord) &&
                    parentRecord.rootId.Equals(rootId))
                {
                    _cascadeParentScratch[record.instanceId] = parentObj.Value;
                }
            }

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (!record.rootId.Equals(rootId))
                    continue;

                if (ChainReaches(record.instanceId, target.instanceId))
                {
                    _removalScratch.Add(record);
                    _removalSetScratch.Add(record.instanceId);
                }
            }
        }

        private bool ChainReaches(PredictedObjectID start, PredictedObjectID targetId)
        {
            var current = start;
            int maxHops = _cascadeParentScratch.Count + 1;
            bool result = false;

            for (var hop = 0; hop <= maxHops; hop++)
            {
                if (current.Equals(targetId))
                {
                    result = true;
                    break;
                }

                if (_cascadeReachScratch.TryGetValue(current, out var known))
                {
                    result = known;
                    break;
                }

                if (!_cascadeParentScratch.TryGetValue(current, out var parent))
                    break;

                current = parent;
            }

            _cascadeReachScratch[start] = result;
            return result;
        }

        private PredictedObjectID? EffectiveParentObject(in InstanceDetails record)
        {
            if (_instanceMap.TryGetValue(record.instanceId, out var go) && go &&
                go.TryGetComponent<PredictedParent>(out var carrier))
            {
                var link = carrier.resolvedParent;
                return link?.objectId;
            }

            var def = GetDefaultParent(record);
            return def?.objectId;
        }

        private void PromoteOrphans(InstanceDetails rootRecord)
        {
            var rootId = rootRecord.rootId;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (!record.rootId.Equals(rootId) || record.instanceId.Equals(rootRecord.instanceId))
                    continue;

                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                go.transform.GetPositionAndRotation(out var pos, out var rot);
                var promoted = new InstanceDetails(record.prefabId, record.pieceIndex.value, record.instanceId, pos, rot, rootRecord.owner, record.parent);

                _spawnedPrefabs[i] = promoted;
                _recordsById[record.instanceId] = promoted;
            }
        }

        private void RemovePieceSet(List<InstanceDetails> records, HashSet<PredictedObjectID> memberSet,
            bool canPool, bool triggerDestroyEvent, bool removeRecords)
        {
            _removedRecordScratch.Clear();
            bool progress = true;

            while (progress)
            {
                progress = false;

                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];

                    if (!memberSet.Contains(record.instanceId))
                        continue;

                    if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    {
                        _instanceMap.Remove(record.instanceId);
                        if (removeRecords)
                        {
                            _recordsById.Remove(record.instanceId);
                            _removedRecordScratch.Add(record.instanceId);
                        }
                        memberSet.Remove(record.instanceId);
                        progress = true;
                        continue;
                    }

                    bool isTopmost = true;
                    var ancestor = go.transform.parent;

                    while (ancestor != null)
                    {
                        if (_goToId.TryGetValue(ancestor.gameObject, out var ancestorId) && memberSet.Contains(ancestorId))
                        {
                            isTopmost = false;
                            break;
                        }

                        ancestor = ancestor.parent;
                    }

                    if (!isTopmost)
                        continue;

                    RemoveSubtree(record, go, memberSet, canPool, triggerDestroyEvent, removeRecords);
                    progress = true;
                }
            }

            if (removeRecords && _removedRecordScratch.Count > 0)
            {
                int w = 0;

                for (var i = 0; i < _spawnedPrefabs.Count; i++)
                {
                    if (!_removedRecordScratch.Contains(_spawnedPrefabs[i].instanceId))
                        _spawnedPrefabs[w++] = _spawnedPrefabs[i];
                }

                _spawnedPrefabs.RemoveRange(w, _spawnedPrefabs.Count - w);
                _removedRecordScratch.Clear();
            }
        }

        private void RemoveSubtree(InstanceDetails topRecord, GameObject topGo, HashSet<PredictedObjectID> memberSet,
            bool canPool, bool triggerDestroyEvent, bool removeRecords)
        {
            _memberPiecesScratch.Clear();
            RescueAndCollect(topGo.transform, memberSet, _memberPiecesScratch);

            for (var i = 0; i < _memberPiecesScratch.Count; i++)
            {
                var piece = _memberPiecesScratch[i];

                predictionManager.UnregisterInstance(piece.gameObject, false, triggerDestroyEvent);

                _instanceMap.Remove(piece.id);
                _goToId.Remove(piece.gameObject);
                memberSet.Remove(piece.id);

                if (removeRecords)
                {
                    _recordsById.Remove(piece.id);
                    _removedRecordScratch.Add(piece.id);
                }
            }

            var proto = GetPrototype(topRecord.prefabId);
            bool isComplete = topRecord.isRootRecord && proto != null && _memberPiecesScratch.Count == proto.pieceCount;

            if (canPool)
            {
                bool detach = !topRecord.isRootRecord || TryResolveParentLink(topGo, out _);

                if (detach && topGo.transform.parent != null)
                    topGo.transform.SetParent(null, true);

                _pool.PutTree(topRecord.prefabId, topRecord.instanceId, topRecord.spawnPosition, topGo,
                    _memberPiecesScratch, predictionManager.localTick, isComplete);

                topGo.SetActive(false);
            }
            else
            {
                if (isComplete)
                    predictionManager.InternalDelete(topRecord.prefabId, topGo);
                else
                    UnityProxy.DestroyImmediateDirectly(topGo);
            }
        }

        private void RescueAndCollect(Transform current, HashSet<PredictedObjectID> memberSet, List<PooledPiece> members)
        {
            if (_goToId.TryGetValue(current.gameObject, out var pieceId))
            {
                if (!memberSet.Contains(pieceId))
                {
                    if (current.parent != null)
                        current.SetParent(null, true);
                    return;
                }

                uint pieceIndex = _recordsById.TryGetValue(pieceId, out var record) ? record.pieceIndex.value : 0;
                members.Add(new PooledPiece(pieceId, pieceIndex, current.gameObject));
            }

            for (var i = current.childCount - 1; i >= 0; i--)
                RescueAndCollect(current.GetChild(i), memberSet, members);
        }

        private void RemoveRecord(PredictedObjectID id)
        {
            if (!_recordsById.Remove(id))
                return;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                if (_spawnedPrefabs[i].instanceId.Equals(id))
                {
                    _spawnedPrefabs.RemoveAt(i);
                    return;
                }
            }
        }

        public void Delete(GameObject go)
        {
            if (!go)
                return;

            EnqueueTopmostPieces(go.transform);
        }

        private void EnqueueTopmostPieces(Transform trs)
        {
            if (_goToId.TryGetValue(trs.gameObject, out var poid))
            {
                currentState.toDelete.Add(poid);
                return;
            }

            int children = trs.childCount;

            for (int i = 0; i < children; i++)
                EnqueueTopmostPieces(trs.GetChild(i));
        }

        public void Delete(PredictedIdentity pid)
        {
            if (pid)
                Delete(pid.gameObject);
        }

        public void Delete(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return;

            if (id.TryGetGameObject(predictionManager, out var go))
                Delete(go);
            else if (_recordsById.ContainsKey(id.Value))
                currentState.toDelete.Add(id.Value);
        }

        public void Cleanup()
        {
            _isCleaningUp = true;
            try
            {
                CleanupInternal();
            }
            finally
            {
                _isCleaningUp = false;
            }
        }

        private void CleanupInternal()
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                if (_isSceneObject.Contains(record.instanceId))
                {
                    predictionManager.UnregisterInstance(go, true, true);
                    continue;
                }

                predictionManager.UnregisterInstance(go, false, true);
            }

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (_isSceneObject.Contains(record.instanceId))
                    continue;

                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                UnityProxy.DestroyImmediateDirectly(go);
            }

            _instanceMap.Clear();
            _goToId.Clear();
            _spawnedPrefabs.Clear();
            _recordsById.Clear();
            _isSceneObject.Clear();
            _reservedSceneObjects.Clear();
            _pendingSceneReservations.Clear();
            _policyRefreshStamp.Clear();
            _parentingWarned.Clear();
            _pool.Clear(predictionManager);
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
