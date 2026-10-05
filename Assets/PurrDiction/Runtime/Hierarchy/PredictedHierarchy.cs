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
        readonly Dictionary<PredictedObjectID, InstanceDetails> _recordsById = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        readonly Dictionary<GameObject, PredictedObjectID> _goToId = new ();
        readonly HashSet<PredictedObjectID> _isSceneObject = new ();
        readonly List<PredictedObjectID> _reservedSceneObjects = new ();
        readonly Dictionary<int, PiecePrototype> _prototypes = new ();
        readonly PredictedPiecePool _pool = new ();

        private PredictedNetworkMirror _networkMirror;

        internal PredictedNetworkMirror networkMirror => _networkMirror ??= new PredictedNetworkMirror(predictionManager, this);

        internal bool TryGetRecord(PredictedObjectID id, out InstanceDetails record)
            => _recordsById.TryGetValue(id, out record);

        internal void SyncNetworkMirror()
        {
            if (!predictionManager || predictionManager.isServer)
                return;

            if (!TryGetVerifiedState(predictionManager.localTick, out _, out var verified) || verified.spawnedPrefabs.isDisposed)
                return;

            networkMirror.SyncVerified(verified.spawnedPrefabs);
        }

        private static NetworkID? PieceNetworkId(NetworkID? block, PiecePrototype proto, int pieceIndex)
        {
            if (!block.HasValue || proto.pieces[pieceIndex].networkIdentityCount == 0)
                return null;
            return new NetworkID(block.Value, (ulong)proto.pieces[pieceIndex].networkIdOffset);
        }

        internal bool ContainsPooledObject(PredictedObjectID id) => _pool.Contains(id);

        internal bool CollectVerifiedPieceIds(ulong fromTick, HashSet<PredictedObjectID> result)
        {
            var history = verifiedStateHistory;
            if (history == null || history.Count == 0)
                return false;

            int start = history.Find(fromTick, out int index) ? index : System.Math.Max(0, index - 1);
            for (var i = start; i < history.Count; i++)
            {
                var pieces = history[i].state.spawnedPrefabs;
                if (pieces.isDisposed)
                    continue;

                for (var p = 0; p < pieces.Count; p++)
                    result.Add(pieces[p].instanceId);
            }

            return true;
        }

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
        readonly Dictionary<PredictedObjectID, List<InstanceDetails>> _cascadeGroups = new ();
        readonly List<PredictedObjectID> _cascadeGroupOrder = new ();
        readonly Dictionary<PredictedObjectID, int> _cascadeGroupDepth = new ();
        CascadeGroupComparer _cascadeGroupComparer;
        readonly HashSet<PredictedObjectID> _cascadeMemberScratch = new ();
        readonly List<InstanceDetails> _cascadeRootScratch = new ();
        readonly Dictionary<PredictedObjectID, DecorationAnchor> _pendingDecorationRestores = new ();
        readonly List<PredictedObjectID> _decorationRestoreScratch = new ();

        readonly Dictionary<PredictedObjectID, HashSet<PredictedObjectID>> _visibilityParentsByRoot = new ();
        readonly Dictionary<PredictedObjectID, HashSet<PredictedObjectID>> _visibilityChildrenByRoot = new ();
        readonly Queue<PredictedObjectID> _visibilityDependencyQueue = new ();

        bool _visibilityDependencyCacheDirty = true;
        bool _hasCrossRootVisibilityDependencies;

        internal bool hasCrossRootVisibilityDependencies
        {
            get
            {
                if (_visibilityDependencyCacheDirty)
                    RefreshVisibilityDependencyCache();
                return _hasCrossRootVisibilityDependencies;
            }
        }

        void InvalidateVisibilityTopology()
        {
            _visibilityDependencyCacheDirty = true;
            if (predictionManager)
                predictionManager.HandleVisibilityTopologyChanged();
        }

        internal void NotifyVisibilityParentLinkInvalidated()
        {
            InvalidateVisibilityTopology();
        }

        readonly Stack<HashSet<PredictedObjectID>> _visibilityDependencySetPool = new ();

        void RecycleVisibilityDependencyGraph(
            Dictionary<PredictedObjectID, HashSet<PredictedObjectID>> graph)
        {
            foreach (var dependencies in graph.Values)
            {
                dependencies.Clear();
                _visibilityDependencySetPool.Push(dependencies);
            }

            graph.Clear();
        }

        void RefreshVisibilityDependencyCache()
        {
            RecycleVisibilityDependencyGraph(_visibilityParentsByRoot);
            RecycleVisibilityDependencyGraph(_visibilityChildrenByRoot);
            _hasCrossRootVisibilityDependencies = false;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                var parentObject = EffectiveParentObject(record);
                if (!parentObject.HasValue ||
                    !_recordsById.TryGetValue(parentObject.Value, out var parentRecord) ||
                    parentRecord.rootId.Equals(record.rootId))
                {
                    continue;
                }

                _hasCrossRootVisibilityDependencies = true;
                AddVisibilityDependency(
                    _visibilityParentsByRoot,
                    record.rootId,
                    parentRecord.rootId);
                AddVisibilityDependency(
                    _visibilityChildrenByRoot,
                    parentRecord.rootId,
                    record.rootId);
            }

            _visibilityDependencyCacheDirty = false;
        }

        void AddVisibilityDependency(
            Dictionary<PredictedObjectID, HashSet<PredictedObjectID>> graph,
            PredictedObjectID rootId,
            PredictedObjectID dependency)
        {
            if (!graph.TryGetValue(rootId, out var dependencies))
            {
                dependencies = _visibilityDependencySetPool.Count > 0
                    ? _visibilityDependencySetPool.Pop()
                    : new HashSet<PredictedObjectID>();
                graph.Add(rootId, dependencies);
            }

            dependencies.Add(dependency);
        }

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

        readonly HashSet<PredictedObjectID> _verifiedApplyEntrants = new ();
        readonly HashSet<PredictedObjectID> _replacedEntrantsScratch = new ();

        readonly Dictionary<PredictedObjectID, SpawnKey> _spawnKeys = new ();
        readonly Dictionary<PredictedComponentID, int> _spawnOrdinals = new ();
        uint _spawnOrdinalPass = uint.MaxValue;

        private SpawnKey NextSpawnKey()
        {
            var creator = predictionManager.spawnCreator;
            if (!creator.HasValue)
                return default;

            if (_spawnOrdinalPass != predictionManager.spawnPass)
            {
                _spawnOrdinals.Clear();
                _spawnOrdinalPass = predictionManager.spawnPass;
            }

            _spawnOrdinals.TryGetValue(creator.Value, out var ordinal);
            _spawnOrdinals[creator.Value] = ordinal + 1;
            return new SpawnKey(predictionManager.localTickInContext, creator.Value, ordinal);
        }

        internal bool WasMaterializedByVerifiedApply(PredictedObjectID id)
            => _verifiedApplyEntrants.Contains(id);

        internal void ApplyHistoricalTopology(in PredictedHierarchyState state)
        {
            currentState.Dispose();
            currentState = state.Duplicate();
            SetUnityState(currentState);
        }

        protected override void SetUnityState(PredictedHierarchyState state)
        {
            _isRollingBack = true;
            _verifiedApplyEntrants.Clear();
            _replacedEntrantsScratch.Clear();

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
                    {
                        if (targetRecord.owner == record.owner)
                            continue;

                        _replacedEntrantsScratch.Add(record.instanceId);
                    }
                }

                _removalScratch.Add(record);
                _removalSetScratch.Add(record.instanceId);
            }

            if (_removalScratch.Count > 0)
            {
                _cascadeGroups.Clear();
                _cascadeGroupOrder.Clear();

                for (var i = 0; i < _removalScratch.Count; i++)
                {
                    var removal = _removalScratch[i];
                    var removalRoot = removal.rootId;

                    if (!_cascadeGroups.TryGetValue(removalRoot, out var group))
                    {
                        group = ListPool<InstanceDetails>.Instantiate();
                        _cascadeGroups[removalRoot] = group;
                        _cascadeGroupOrder.Add(removalRoot);
                    }

                    group.Add(removal);
                }

                for (var g = 0; g < _cascadeGroupOrder.Count; g++)
                {
                    var group = _cascadeGroups[_cascadeGroupOrder[g]];

                    _cascadeMemberScratch.Clear();
                    for (var i = 0; i < group.Count; i++)
                        _cascadeMemberScratch.Add(group[i].instanceId);

                    RemovePieceSet(group, _cascadeMemberScratch, true, false, false);
                    ListPool<InstanceDetails>.Destroy(group);
                }

                _cascadeGroups.Clear();
                _cascadeGroupOrder.Clear();
            }

            _additionGroups.Clear();
            _additionGroupOrder.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];

                if (_instanceMap.ContainsKey(record.instanceId))
                    continue;

                var rootId = record.rootId;

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

                for (var r = 0; r < records.Count; r++)
                {
                    if (_instanceMap.ContainsKey(records[r].instanceId))
                        _verifiedApplyEntrants.Add(records[r].instanceId);
                }

                ListPool<InstanceDetails>.Destroy(records);
            }

            _spawnedPrefabs.Clear();
            _recordsById.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];
                _spawnedPrefabs.Add(record);
                _recordsById[record.instanceId] = record;
            }

            _nextInstanceId = state.nextInstanceId;

            RunDecorationRestores();

            _replacedEntrantsScratch.Clear();
            _isRollingBack = false;
            InvalidateVisibilityTopology();
        }

        private sealed class CascadeGroupComparer : IComparer<PredictedObjectID>
        {
            readonly Dictionary<PredictedObjectID, int> _depth;

            public CascadeGroupComparer(Dictionary<PredictedObjectID, int> depth) => _depth = depth;

            public int Compare(PredictedObjectID a, PredictedObjectID b)
            {
                int cmp = _depth[b].CompareTo(_depth[a]);
                return cmp != 0 ? cmp : a.instanceId.value.CompareTo(b.instanceId.value);
            }
        }

        private struct DecorationAnchor
        {
            public PredictedObjectID? anchorId;
            public int[] descendPath;
            public Transform plainParent;
            public bool isPlain;
        }

        private enum DecorationRestoreResult
        {
            KeepPending,
            Restored,
            Evict
        }

        private void CaptureDecoration(PredictedObjectID pieceId, GameObject go)
        {
            if (_isCleaningUp)
                return;

            var directParent = go.transform.parent;

            if (directParent == null)
            {
                _pendingDecorationRestores.Remove(pieceId);
                return;
            }

            if (go.TryGetComponent<PredictedParent>(out _))
                return;

            if (!_recordsById.TryGetValue(pieceId, out var record))
                return;

            if (!TryResolveParentLink(go, out var link))
            {
                _pendingDecorationRestores[pieceId] = new DecorationAnchor
                {
                    plainParent = directParent,
                    isPlain = true
                };
                return;
            }

            if (SameAttach(link, GetDefaultParent(record)))
            {
                _pendingDecorationRestores.Remove(pieceId);
                return;
            }

            if (!predictionManager.TryGetIdentity(link, out var anchorIdentity) || !anchorIdentity)
                return;

            var anchorTrs = anchorIdentity.transform;
            int depth = 0;
            var current = directParent;

            while (current != null && current != anchorTrs)
            {
                depth++;
                current = current.parent;
            }

            if (current == null)
                return;

            var descend = depth == 0 ? System.Array.Empty<int>() : new int[depth];
            current = directParent;

            for (var i = 0; i < depth; i++)
            {
                descend[i] = current.GetSiblingIndex();
                current = current.parent;
            }

            _pendingDecorationRestores[pieceId] = new DecorationAnchor
            {
                anchorId = link.objectId,
                descendPath = descend
            };
        }

        private DecorationRestoreResult TryRestoreDecoration(PredictedObjectID pieceId, in DecorationAnchor anchor)
        {
            if (!_instanceMap.TryGetValue(pieceId, out var go) || !go)
                return DecorationRestoreResult.KeepPending;

            if (go.TryGetComponent<PredictedParent>(out _))
                return DecorationRestoreResult.Evict;

            Transform target;

            if (anchor.isPlain)
            {
                if (!anchor.plainParent)
                    return DecorationRestoreResult.Evict;

                target = anchor.plainParent;
            }
            else
            {
                if (!anchor.anchorId.HasValue ||
                    !_instanceMap.TryGetValue(anchor.anchorId.Value, out var anchorGo) || !anchorGo)
                {
                    return DecorationRestoreResult.KeepPending;
                }

                target = anchorGo.transform;
                var path = anchor.descendPath;

                for (var i = path.Length - 1; i >= 0; i--)
                {
                    if (path[i] >= target.childCount)
                        break;

                    target = target.GetChild(path[i]);
                }
            }

            var trs = go.transform;

            if (target == trs || target.IsChildOf(trs))
                return DecorationRestoreResult.Evict;

            if (trs.parent != target)
            {
                trs.SetParent(target, true);
                NotifyInstanceParentChanged(go);
            }

            return DecorationRestoreResult.Restored;
        }

        private void RunDecorationRestores()
        {
            if (_pendingDecorationRestores.Count == 0)
                return;

            _decorationRestoreScratch.Clear();

            foreach (var pending in _pendingDecorationRestores)
            {
                if (TryRestoreDecoration(pending.Key, pending.Value) != DecorationRestoreResult.KeepPending)
                    _decorationRestoreScratch.Add(pending.Key);
            }

            for (var i = 0; i < _decorationRestoreScratch.Count; i++)
                _pendingDecorationRestores.Remove(_decorationRestoreScratch[i]);

            _decorationRestoreScratch.Clear();
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

            NetworkID? networkBlock = null;
            if (proto.networkIdentityCount > 0 && networkMirror.TryReserveBlock(proto, out var reservedBlock))
                networkBlock = reservedBlock;

            _recordBuildScratch.Add(new InstanceDetails(prefabId, 0, rootId, position, rotation, owner, parent, PieceNetworkId(networkBlock, proto, 0)));

            for (var k = 1; k < proto.pieceCount; k++)
                _recordBuildScratch.Add(new InstanceDetails(prefabId, (uint)k, new PredictedObjectID(baseId + (uint)k), Vector3.zero, Quaternion.identity, null, null, PieceNetworkId(networkBlock, proto, k)));

            var rootGo = CreateWholeInstance(prefabId, proto, _recordBuildScratch, NextSpawnKey());

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

        private GameObject CreateWholeInstance(int prefabId, PiecePrototype proto, List<InstanceDetails> records,
            in SpawnKey spawnKey = default)
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
            bool sameSpawn = false;
            GameObject rootGo = null;

            Transform parentTrs = null;

            if (hasRoot && rootRecord.parent.HasValue)
            {
                if (predictionManager.TryGetIdentity(rootRecord.parent.Value, out var parentIdentity) && parentIdentity)
                    parentTrs = parentIdentity.transform;
                else
                    PurrLogger.LogError($"Failed to resolve spawn parent {rootRecord.parent.Value} for prefab {prefabId}; spawning unparented.");
            }

            if (hasRoot)
            {
                ulong tick = predictionManager.localTickInContext;
                if (_pool.TryTakeSameSpawn(prefabId, spawnKey, rootRecord.instanceId, _takenPiecesScratch, out rootGo))
                {
                    sameSpawn = true;
                }
                else if (!_pool.TryTakeTree(rootRecord.instanceId, prefabId, rootRecord.spawnPosition, prefabId >= 0,
                             _takenPiecesScratch, out rootGo, out var drifted, tick))
                {
                    bool reusedExactTree = drifted && _pool.TryTakeExactCompleteTree(
                        rootRecord.instanceId, prefabId, _takenPiecesScratch, out rootGo, tick);
                    if (!reusedExactTree && (drifted || prefabId < 0))
                        _pool.TryTakeNearestCompleteTree(prefabId, rootRecord.spawnPosition, _takenPiecesScratch, out rootGo, tick);
                }

                if (spawnKey.isValid)
                    _spawnKeys[rootRecord.instanceId] = spawnKey;
                else
                    _spawnKeys.Remove(rootRecord.instanceId);

                if (rootGo)
                {
                    if (!PreservesSoftCorrectionRootPose(
                            rootGo,
                            rootRecord.instanceId,
                            rootRecord.owner))
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

                // Fuzzy fallback or fresh instantiation may bypass a pool claim for this live id.
                // Release it to prevent the pool from resurrecting a duplicate.
                _pool.ReleaseClaim(record.instanceId);

                _instanceMap[record.instanceId] = pieceGo;
                _goToId[pieceGo] = record.instanceId;

                bool recordReset = reset || _replacedEntrantsScratch.Contains(record.instanceId);
                predictionManager.RegisterInstance(pieceGo, record.instanceId, instanceOwner, recordReset, removedFromPoolEvent,
                    sameSpawn && !_individuallyTakenScratch.Contains(record.pieceIndex.value));
                networkMirror.OnPieceMaterialized(record, pieceGo, proto, instanceOwner);
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

            _pool.ReleaseClaim(record.instanceId);

            _instanceMap[record.instanceId] = pieceGo;
            _goToId[pieceGo] = record.instanceId;

            predictionManager.RegisterInstance(
                pieceGo,
                record.instanceId,
                recordOwner,
                _replacedEntrantsScratch.Contains(record.instanceId),
                false);
            networkMirror.OnPieceMaterialized(record, pieceGo, proto, recordOwner);
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
            bool hasStamp = _policyRefreshStamp.TryGetValue(go, out var stamp);
            bool topologyChanged = !hasStamp || stamp.parent != currentParent;

            if (!hasStamp || stamp.frame != frame || topologyChanged)
            {
                _policyRefreshStamp[go] = (frame, currentParent);
                RefreshDescendantPolicies(go);
            }

            if (topologyChanged)
                InvalidateVisibilityTopology();

            if (_isRollingBack || _suppressParentWarnings || predictionManager.isReplaying)
                return;

            _pendingDecorationRestores.Remove(instanceId);

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

            _policyRefreshStamp.Remove(go);
            predictionManager.CapturePendingVisibilityDelete(instanceId);
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

        private bool PreservesSoftCorrectionRootPose(
            GameObject instance,
            PredictedObjectID instanceId,
            PlayerID? owner)
        {
            if (!predictionManager.isReplaying || !instance)
                return false;

            return instance.TryGetComponent(out PredictedTransform predictedTransform) &&
                   predictedTransform.id.objectId.Equals(instanceId) &&
                   predictedTransform.owner == owner &&
                   predictedTransform.previousRegisteredEffectivePredictionPolicy == PredictionPolicy.SoftCorrection &&
                   predictedTransform.ResolveEffectivePredictionPolicyForSetup(owner, predictionManager) ==
                       PredictionPolicy.SoftCorrection;
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

        /// <summary>
        /// True when both pieces belong to the same spawn instance.
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
                PurrLogger.LogError($"Deleting {id} which has a record but no live instance; removing the stale record.");
                predictionManager.CapturePendingVisibilityDelete(id);
                _instanceMap.Remove(id);
                RemoveRecord(id);
                return;
            }

            _removalScratch.Clear();
            _removalSetScratch.Clear();
            CollectCascade(record);

            _cascadeGroups.Clear();
            _cascadeGroupOrder.Clear();
            _cascadeGroupDepth.Clear();
            _cascadeRootScratch.Clear();

            for (var i = 0; i < _removalScratch.Count; i++)
            {
                var removal = _removalScratch[i];
                var removalRoot = removal.rootId;
                int depth = ChainDepthTo(removal.instanceId, record.instanceId);

                if (!_cascadeGroups.TryGetValue(removalRoot, out var group))
                {
                    group = ListPool<InstanceDetails>.Instantiate();
                    _cascadeGroups[removalRoot] = group;
                    _cascadeGroupOrder.Add(removalRoot);
                    _cascadeGroupDepth[removalRoot] = depth;
                }
                else if (depth < _cascadeGroupDepth[removalRoot])
                {
                    _cascadeGroupDepth[removalRoot] = depth;
                }

                group.Add(removal);

                if (removal.isRootRecord)
                    _cascadeRootScratch.Add(removal);
            }

            _cascadeGroupComparer ??= new CascadeGroupComparer(_cascadeGroupDepth);
            _cascadeGroupOrder.Sort(_cascadeGroupComparer);

            var isVerified = predictionManager.isVerified;

            _suppressParentWarnings = true;
            try
            {
                for (var g = 0; g < _cascadeGroupOrder.Count; g++)
                {
                    var group = _cascadeGroups[_cascadeGroupOrder[g]];
                    bool canPool = group[0].prefabId.value < 0 || !isVerified;

                    _cascadeMemberScratch.Clear();
                    for (var i = 0; i < group.Count; i++)
                        _cascadeMemberScratch.Add(group[i].instanceId);

                    RemovePieceSet(group, _cascadeMemberScratch, canPool, true, true);
                }
            }
            finally
            {
                _suppressParentWarnings = false;
            }

            for (var i = 0; i < _cascadeRootScratch.Count; i++)
                PromoteOrphans(_cascadeRootScratch[i]);

            RebaseSurvivorsOfDeletedParents();

            foreach (var group in _cascadeGroups.Values)
                ListPool<InstanceDetails>.Destroy(group);

            _cascadeGroups.Clear();
            _cascadeGroupOrder.Clear();
            _cascadeGroupDepth.Clear();
            _cascadeRootScratch.Clear();
        }

        private void CollectCascade(in InstanceDetails target)
        {
            _cascadeParentScratch.Clear();
            _cascadeReachScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                var parentObj = EffectiveParentObject(record);

                if (parentObj.HasValue && _recordsById.ContainsKey(parentObj.Value))
                    _cascadeParentScratch[record.instanceId] = parentObj.Value;
            }

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (ChainReaches(record.instanceId, target.instanceId))
                {
                    _removalScratch.Add(record);
                    _removalSetScratch.Add(record.instanceId);
                }
            }
        }

        private int ChainDepthTo(PredictedObjectID start, PredictedObjectID targetId)
        {
            var current = start;
            int maxHops = _cascadeParentScratch.Count + 1;
            int depth = 0;

            for (var hop = 0; hop <= maxHops; hop++)
            {
                if (current.Equals(targetId))
                    return depth;

                if (!_cascadeParentScratch.TryGetValue(current, out var parent))
                    return depth;

                current = parent;
                depth++;
            }

            return depth;
        }

        private void RebaseSurvivorsOfDeletedParents()
        {
            bool changed = false;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (!record.parent.HasValue || _recordsById.ContainsKey(record.parent.Value.objectId))
                    continue;

                var position = record.spawnPosition;
                var rotation = record.spawnRotation;

                if (_instanceMap.TryGetValue(record.instanceId, out var go) && go)
                    go.transform.GetPositionAndRotation(out position, out rotation);

                var rebased = new InstanceDetails(record.prefabId, record.pieceIndex.value, record.instanceId,
                    position, rotation, record.owner, null, record.networkId);

                _spawnedPrefabs[i] = rebased;
                _recordsById[record.instanceId] = rebased;
                changed = true;
            }

            if (changed)
                InvalidateVisibilityTopology();
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
                var promoted = new InstanceDetails(record.prefabId, record.pieceIndex.value, record.instanceId, pos, rot, rootRecord.owner, record.parent, record.networkId);

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
                InvalidateVisibilityTopology();
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

                CaptureDecoration(piece.id, piece.gameObject);
                _networkMirror?.OnPieceUnmaterialized(piece.id);
                predictionManager.UnregisterInstance(piece.gameObject, false, triggerDestroyEvent);

                _instanceMap.Remove(piece.id);
                _goToId.Remove(piece.gameObject);
                _policyRefreshStamp.Remove(piece.gameObject);
                memberSet.Remove(piece.id);

                if (removeRecords)
                {
                    _recordsById.Remove(piece.id);
                    _removedRecordScratch.Add(piece.id);
                }
            }

            var proto = GetPrototype(topRecord.prefabId);
            bool isComplete = topRecord.isRootRecord && proto != null && _memberPiecesScratch.Count == proto.pieceCount;
            _spawnKeys.Remove(topRecord.instanceId, out var spawnKey);

            if (canPool)
            {
                bool detach = !topRecord.isRootRecord || TryResolveParentLink(topGo, out _);

                if (detach && topGo.transform.parent != null)
                    topGo.transform.SetParent(null, true);

                _pool.PutTree(topRecord.prefabId, topRecord.instanceId, topRecord.spawnPosition, topGo,
                    _memberPiecesScratch, predictionManager.localTick, isComplete, isComplete ? spawnKey : default);

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
                    CaptureDecoration(pieceId, current.gameObject);

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
                    InvalidateVisibilityTopology();
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
                predictionManager.CapturePendingVisibilityDelete(poid);
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
            if (id.TryGetGameObject(predictionManager, out var go))
                Delete(go);
        }

        internal void CollectSpawnedRoots(HashSet<PredictedObjectID> roots)
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
                roots.Add(_spawnedPrefabs[i].rootId);
        }

        internal bool ContainsSpawnedRoot(PredictedObjectID rootId)
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                if (_spawnedPrefabs[i].rootId.Equals(rootId))
                    return true;
            }

            return false;
        }

        internal void ApplyRemoteVisibilityDelete(PredictedObjectID objectId)
        {
            DeleteNow(objectId);
        }

        internal static bool StateContainsInstance(
            in PredictedHierarchyState state,
            PredictedObjectID instanceId)
        {
            if (state.spawnedPrefabs.isDisposed)
                return false;

            for (var i = 0; i < state.spawnedPrefabs.Count; i++)
            {
                if (state.spawnedPrefabs[i].instanceId.Equals(instanceId))
                    return true;
            }

            return false;
        }

        internal void ExpandVisibilityDependencies(HashSet<PredictedObjectID> roots)
        {
            ExpandVisibilityGraph(roots, _visibilityParentsByRoot);
        }

        internal void ExpandVisibilityDependents(HashSet<PredictedObjectID> roots)
        {
            ExpandVisibilityGraph(roots, _visibilityChildrenByRoot);
        }

        void ExpandVisibilityGraph(
            HashSet<PredictedObjectID> roots,
            Dictionary<PredictedObjectID, HashSet<PredictedObjectID>> graph)
        {
            if (_visibilityDependencyCacheDirty)
                RefreshVisibilityDependencyCache();

            _visibilityDependencyQueue.Clear();
            foreach (var rootId in roots)
                _visibilityDependencyQueue.Enqueue(rootId);

            while (_visibilityDependencyQueue.Count > 0)
            {
                var rootId = _visibilityDependencyQueue.Dequeue();
                if (!graph.TryGetValue(rootId, out var dependencies))
                    continue;

                foreach (var dependency in dependencies)
                {
                    if (roots.Add(dependency))
                        _visibilityDependencyQueue.Enqueue(dependency);
                }
            }
        }

        internal static PredictedHierarchyState BuildVisibilityProjection(
            in PredictedHierarchyState source,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            int sourceCount = source.spawnedPrefabs.isDisposed ? 0 : source.spawnedPrefabs.Count;
            int deleteCount = source.toDelete.isDisposed ? 0 : source.toDelete.Count;
            var spawned = DisposableList<InstanceDetails>.Create(sourceCount);
            var deletes = DisposableList<PredictedObjectID>.Create(deleteCount);

            bool hasPreviousRoot = false;
            bool previousRootVisible = false;
            PredictedObjectID previousRoot = default;

            for (var i = 0; i < sourceCount; i++)
            {
                var record = source.spawnedPrefabs[i];
                var rootId = record.rootId;
                if (!hasPreviousRoot || !rootId.Equals(previousRoot))
                {
                    previousRoot = rootId;
                    previousRootVisible = timeline.WasVisibleAt(rootId, tick);
                    hasPreviousRoot = true;
                }

                if (previousRootVisible)
                    spawned.Add(record);
            }

            Dictionary<PredictedObjectID, PredictedObjectID> rootByPiece = null;
            try
            {
                if (deleteCount > 1)
                {
                    rootByPiece =
                        DictionaryPool<PredictedObjectID, PredictedObjectID>.Instantiate();
                    for (var i = 0; i < sourceCount; i++)
                    {
                        var record = source.spawnedPrefabs[i];
                        rootByPiece[record.instanceId] = record.rootId;
                    }
                }

                for (var i = 0; i < deleteCount; i++)
                {
                    var deleteId = source.toDelete[i];
                    PredictedObjectID rootId;
                    bool resolved = rootByPiece != null
                        ? rootByPiece.TryGetValue(deleteId, out rootId)
                        : TryResolveRoot(in source, deleteId, out rootId);

                    if (resolved && timeline.WasVisibleAt(rootId, tick))
                        deletes.Add(deleteId);
                }
            }
            finally
            {
                if (rootByPiece != null)
                {
                    DictionaryPool<PredictedObjectID, PredictedObjectID>.Destroy(
                        rootByPiece);
                }
            }

            return new PredictedHierarchyState(spawned, deletes, source.nextInstanceId);
        }

        internal static void CollectSpawnedRoots(
            in PredictedHierarchyState state,
            HashSet<PredictedObjectID> roots)
        {
            if (state.spawnedPrefabs.isDisposed)
                return;

            for (var i = 0; i < state.spawnedPrefabs.Count; i++)
                roots.Add(state.spawnedPrefabs[i].rootId);
        }

        internal static bool StateContainsRoot(in PredictedHierarchyState state, PredictedObjectID rootId)
        {
            if (state.spawnedPrefabs.isDisposed)
                return false;

            for (var i = 0; i < state.spawnedPrefabs.Count; i++)
            {
                if (state.spawnedPrefabs[i].rootId.Equals(rootId))
                    return true;
            }

            return false;
        }

        static bool TryResolveRoot(
            in PredictedHierarchyState state,
            PredictedObjectID pieceId,
            out PredictedObjectID rootId)
        {
            if (!state.spawnedPrefabs.isDisposed)
            {
                for (var i = 0; i < state.spawnedPrefabs.Count; i++)
                {
                    var record = state.spawnedPrefabs[i];
                    if (!record.instanceId.Equals(pieceId))
                        continue;

                    rootId = record.rootId;
                    return true;
                }
            }

            rootId = default;
            return false;
        }

        internal void RunWriteFirstVisibilityState(
            ulong tick,
            BitPacker packer,
            in PredictedHierarchyState projection)
        {
            WriteFirstDynamicModuleSnapshot(tick, packer);
            WriteFirstStateModules(tick, packer);
            WriteFirstProjectedState(tick, packer, projection);
        }

        internal bool RunWriteVisibilityState(
            PlayerID receiver,
            BitPacker packer,
            ulong baselineTick,
            in PredictedIdentityState baselinePrediction,
            in PredictedHierarchyState baseline,
            in PredictedHierarchyState current)
        {
            bool moduleSetChanged = WriteDynamicModuleSnapshot(receiver, packer, baselineTick);
            bool modulesChanged = WriteModules(receiver, packer, baselineTick);
            bool hierarchyChanged = WriteProjectedState(
                packer,
                baselinePrediction,
                baseline,
                current);

            return moduleSetChanged || modulesChanged || hierarchyChanged;
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
            _networkMirror?.Clear();

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
            _verifiedApplyEntrants.Clear();
            _spawnKeys.Clear();
            _spawnOrdinals.Clear();
            _pendingDecorationRestores.Clear();
            _visibilityParentsByRoot.Clear();
            _visibilityChildrenByRoot.Clear();
            _visibilityDependencyQueue.Clear();
            InvalidateVisibilityTopology();
            _pool.Clear(predictionManager);
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
