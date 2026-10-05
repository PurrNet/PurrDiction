using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// The PurrNet operations the mirror needs. Production uses the scene's HierarchyV2 and
    /// ownership module; tests substitute a recorder so the mirror can run without a transport.
    /// </summary>
    internal interface IPredictedNetworkBridge
    {
        NetworkID ReserveNetworkIds(int count);
        void EarlySpawn(NetworkIdentity identity, NetworkID id);
        void FinalizeSpawn(NetworkIdentity identity);
        void Despawn(NetworkIdentity identity);
        void AddObserver(NetworkIdentity identity, PlayerID player);
        void RemoveObserver(NetworkIdentity identity, PlayerID player);
        void SetOwner(NetworkIdentity identity, PlayerID? owner);
    }

    internal sealed class HierarchyNetworkBridge : IPredictedNetworkBridge
    {
        private readonly HierarchyV2 _hierarchy;
        private readonly GlobalOwnershipModule _ownership;

        private HierarchyNetworkBridge(HierarchyV2 hierarchy, GlobalOwnershipModule ownership)
        {
            _hierarchy = hierarchy;
            _ownership = ownership;
        }

        public static bool TryCreate(NetworkManager manager, SceneID scene, bool asServer, out HierarchyNetworkBridge bridge)
        {
            bridge = null;
            if (!manager || !manager.TryGetModule(asServer, out HierarchyFactory factory) ||
                !factory.TryGetHierarchy(scene, out var hierarchy))
                return false;

            manager.TryGetModule(asServer, out GlobalOwnershipModule ownership);
            bridge = new HierarchyNetworkBridge(hierarchy, ownership);
            return true;
        }

        public NetworkID ReserveNetworkIds(int count) => _hierarchy.ReserveNetworkIDs(count);
        public void EarlySpawn(NetworkIdentity identity, NetworkID id) => _hierarchy.ManualEarlySpawn(identity, id);
        public void FinalizeSpawn(NetworkIdentity identity) => _hierarchy.ManualFinalizeSpawn(identity);
        public void Despawn(NetworkIdentity identity) => _hierarchy.ManualDespawn(identity);
        public void AddObserver(NetworkIdentity identity, PlayerID player) => _hierarchy.ManualAddObserver(identity, player);
        public void RemoveObserver(NetworkIdentity identity, PlayerID player) => _hierarchy.ManualRemoveObserver(identity, player);

        public void SetOwner(NetworkIdentity identity, PlayerID? owner)
        {
            if (_ownership == null)
                return;

            if (owner.HasValue)
                _ownership.GiveOwnership(identity, owner.Value, false, silent: true, isSpawner: true);
            else if (identity.hasOwner)
                _ownership.RemoveOwnership(identity, false, true);
        }
    }

    /// <summary>
    /// Keeps the NetworkIdentity components inside predicted prefabs spawned as a mirror of the
    /// authoritative topology. The server spawns them when an instance enters its topology and
    /// despawns them when it leaves. Clients spawn them only once the instance is part of a
    /// verified frame, so speculative spawns and rollbacks never touch PurrNet. Observers follow
    /// each player's committed predicted visibility once that player has acknowledged the frame
    /// that carries the instance, so buffered RPCs are never sent to a client that cannot route them.
    /// </summary>
    internal sealed class PredictedNetworkMirror
    {
        private struct Entry
        {
            public NetworkID baseId;
            public NetworkIdentity[] identities;
            public PredictedObjectID rootId;
            public ulong spawnTick;
        }

        private readonly PredictionManager _manager;
        private readonly PredictedHierarchy _hierarchy;
        private IPredictedNetworkBridge _bridge;
        private bool _bridgeResolved;

        private readonly Dictionary<PredictedObjectID, Entry> _spawned = new();
        private readonly Dictionary<NetworkIdentity, PredictedObjectID> _pieceByIdentity = new();
        private readonly Dictionary<PlayerID, HashSet<PredictedObjectID>> _observed = new();
        private readonly List<NetworkIdentity> _identityScratch = new();
        private readonly List<PredictedObjectID> _idScratch = new();
        private readonly HashSet<PredictedObjectID> _verifiedScratch = new();

        internal int spawnedPieceCount => _spawned.Count;

        internal PredictedNetworkMirror(PredictionManager manager, PredictedHierarchy hierarchy)
        {
            _manager = manager;
            _hierarchy = hierarchy;
        }

        internal void SetBridge(IPredictedNetworkBridge bridge)
        {
            _bridge = bridge;
            _bridgeResolved = true;
        }

        private IPredictedNetworkBridge bridge
        {
            get
            {
                if (_bridgeResolved)
                    return _bridge;

                if (!_manager || !_manager.isSpawned)
                    return null;

                _bridgeResolved = true;
                if (HierarchyNetworkBridge.TryCreate(_manager.networkManager, _manager.sceneId, _manager.isServer, out var created))
                    _bridge = created;
                return _bridge;
            }
        }

        private bool isServer => _manager && _manager.isServer;

        internal bool IsSpawned(PredictedObjectID pieceId) => _spawned.ContainsKey(pieceId);

        internal bool IsObserving(PlayerID player, PredictedObjectID pieceId)
            => _observed.TryGetValue(player, out var set) && set.Contains(pieceId);

        /// <summary>Server only: allocates the id block for a new instance of <paramref name="proto"/>.</summary>
        internal bool TryReserveBlock(PiecePrototype proto, out NetworkID baseId)
        {
            baseId = default;
            if (proto.networkIdentityCount == 0 || !isServer)
                return false;

            var target = bridge;
            if (target == null)
                return false;

            baseId = target.ReserveNetworkIds(proto.networkIdentityCount);
            return true;
        }

        /// <summary>
        /// A piece with a live GameObject now exists in the local topology. The server spawns its
        /// identities immediately; clients wait for <see cref="SyncVerified"/>.
        /// </summary>
        internal void OnPieceMaterialized(in InstanceDetails record, GameObject go, PiecePrototype proto, PlayerID? owner)
        {
            if (!record.networkId.HasValue || !isServer)
                return;

            Spawn(record, go, proto, owner);
        }

        /// <summary>The piece's GameObject left the topology or went back to the pool.</summary>
        internal void OnPieceUnmaterialized(PredictedObjectID pieceId)
        {
            Despawn(pieceId);
        }

        /// <summary>
        /// Client only: aligns spawned identities with the latest verified topology. Pieces that are
        /// verified, carry an id block and are live get spawned; mirrored pieces that are no longer
        /// verified get despawned.
        /// </summary>
        internal void SyncVerified(DisposableList<InstanceDetails> verified)
        {
            if (isServer)
                return;

            _verifiedScratch.Clear();
            for (var i = 0; i < verified.Count; i++)
            {
                var record = verified[i];
                if (!record.networkId.HasValue)
                    continue;

                _verifiedScratch.Add(record.instanceId);
                if (_spawned.TryGetValue(record.instanceId, out var existing) && existing.baseId == record.networkId.Value)
                    continue;

                if (!_hierarchy.TryGetGameObject(record.instanceId, out var go) || !go)
                    continue;

                var proto = _hierarchy.GetPrototype(record.prefabId);
                if (proto == null)
                    continue;

                var owner = record.owner;
                if (!record.isRootRecord && _hierarchy.TryGetRecord(record.rootId, out var rootRecord))
                    owner = rootRecord.owner;

                Spawn(record, go, proto, owner);
            }

            if (_spawned.Count == 0)
                return;

            _idScratch.Clear();
            foreach (var pieceId in _spawned.Keys)
            {
                if (!_verifiedScratch.Contains(pieceId))
                    _idScratch.Add(pieceId);
            }

            for (var i = 0; i < _idScratch.Count; i++)
                Despawn(_idScratch[i]);
            _idScratch.Clear();
        }

        /// <summary>Server only: the predicted owner of an instance changed.</summary>
        internal void OnOwnershipChanged(PredictedObjectID root, PlayerID? owner, bool cascade)
        {
            if (!isServer)
                return;

            var target = bridge;
            if (target == null)
                return;

            foreach (var (pieceId, entry) in _spawned)
            {
                bool affected = cascade ? entry.rootId.Equals(root) : pieceId.Equals(root);
                if (!affected)
                    continue;

                for (var i = 0; i < entry.identities.Length; i++)
                {
                    var identity = entry.identities[i];
                    if (identity)
                        target.SetOwner(identity, owner);
                }
            }
        }

        /// <summary>
        /// Server only: a player observes a piece's identities exactly while its root is visible to
        /// them and they have acknowledged a frame from after the piece (and its visibility) began.
        /// </summary>
        internal void SyncObservers(PlayerID player, PlayerVisibilityTimeline timeline, ulong acknowledgedTick)
        {
            if (!isServer || _spawned.Count == 0)
                return;

            var target = bridge;
            if (target == null)
                return;

            _observed.TryGetValue(player, out var observed);

            foreach (var (pieceId, entry) in _spawned)
            {
                bool observing = observed != null && observed.Contains(pieceId);
                bool wanted = acknowledgedTick >= entry.spawnTick &&
                              timeline.HasContinuousVisibilityFrom(entry.rootId, acknowledgedTick);

                if (wanted == observing)
                    continue;

                if (wanted)
                {
                    if (observed == null)
                    {
                        observed = new HashSet<PredictedObjectID>();
                        _observed.Add(player, observed);
                    }

                    observed.Add(pieceId);
                    for (var i = 0; i < entry.identities.Length; i++)
                        if (entry.identities[i]) target.AddObserver(entry.identities[i], player);
                }
                else
                {
                    observed.Remove(pieceId);
                    for (var i = 0; i < entry.identities.Length; i++)
                        if (entry.identities[i]) target.RemoveObserver(entry.identities[i], player);
                }
            }

            if (observed != null && observed.Count == 0)
                _observed.Remove(player);
        }

        internal void RemovePlayer(PlayerID player)
        {
            if (!_observed.Remove(player, out var observed))
                return;

            var target = bridge;
            if (target == null)
                return;

            foreach (var pieceId in observed)
            {
                if (!_spawned.TryGetValue(pieceId, out var entry))
                    continue;
                for (var i = 0; i < entry.identities.Length; i++)
                    if (entry.identities[i]) target.RemoveObserver(entry.identities[i], player);
            }
        }

        internal void Clear()
        {
            _idScratch.Clear();
            _idScratch.AddRange(_spawned.Keys);
            for (var i = 0; i < _idScratch.Count; i++)
                Despawn(_idScratch[i]);
            _idScratch.Clear();
            _observed.Clear();
        }

        private void Spawn(in InstanceDetails record, GameObject go, PiecePrototype proto, PlayerID? owner)
        {
            var target = bridge;
            if (target == null)
                return;

            uint k = record.pieceIndex.value;
            if (k >= proto.pieceCount)
                return;

            int expected = proto.pieces[k].networkIdentityCount;
            if (expected == 0)
                return;

            Despawn(record.instanceId);

            _identityScratch.Clear();
            PiecePrototype.CollectNetworkIdentities(go.transform, _identityScratch);

            if (_identityScratch.Count != expected)
            {
                PurrLogger.LogError(
                    $"Piece {record.instanceId} of prefab {record.prefabId} has {_identityScratch.Count} NetworkIdentity components, expected {expected}; network mirror skipped.",
                    go);
                _identityScratch.Clear();
                return;
            }

            var baseId = record.networkId!.Value;
            var identities = _identityScratch.ToArray();
            _identityScratch.Clear();

            for (var i = 0; i < identities.Length; i++)
            {
                // A pooled object can still carry the spawn of a previous piece.
                if (_pieceByIdentity.TryGetValue(identities[i], out var stalePiece))
                    Despawn(stalePiece);
            }

            for (var i = 0; i < identities.Length; i++)
            {
                target.EarlySpawn(identities[i], new NetworkID(baseId, (ulong)i));
                _pieceByIdentity[identities[i]] = record.instanceId;
            }

            if (isServer)
            {
                for (var i = 0; i < identities.Length; i++)
                    target.SetOwner(identities[i], owner);
            }

            for (var i = 0; i < identities.Length; i++)
                target.FinalizeSpawn(identities[i]);

            _spawned[record.instanceId] = new Entry
            {
                baseId = baseId,
                identities = identities,
                rootId = record.rootId,
                // Created during tick T lands in frame T+1; created between ticks lands in frame T.
                spawnTick = _manager.localTick + 1
            };
        }

        private void Despawn(PredictedObjectID pieceId)
        {
            if (!_spawned.Remove(pieceId, out var entry))
                return;

            foreach (var observed in _observed.Values)
                observed.Remove(pieceId);

            var target = bridge;
            for (var i = 0; i < entry.identities.Length; i++)
            {
                var identity = entry.identities[i];
                if (!identity)
                    continue;
                _pieceByIdentity.Remove(identity);
                target?.Despawn(identity);
            }
        }
    }
}
