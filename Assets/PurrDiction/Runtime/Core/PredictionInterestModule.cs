using System;
using System.Collections.Generic;
using PurrNet.Modules;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Overrides interest resolution for one player and predicted root.
    /// </summary>
    public enum PredictionInterestPin : byte
    {
        Default,
        Relevant,
        Culled
    }

    /// <summary>
    /// Customizes the distance-resolved interest tier for each player and predicted root.
    /// </summary>
    public interface IPredictionInterestProvider
    {
        /// <summary>
        /// Returns the tier to pass to pin and ownership resolution.
        /// </summary>
        byte ResolveTier(PredictionManager manager, PlayerID player, PredictedObjectID root, byte computedTier);
    }

    /// <summary>
    /// Provides per-player prediction interest queries, overrides, scheduling, and relevance events.
    /// </summary>
    public sealed class PredictionInterestModule : IDisposable
    {
        internal sealed class RootTarget : ILODTarget
        {
            readonly PredictionInterestModule _module;
            readonly Dictionary<PlayerID, byte> _computedTiers = new ();
            readonly Dictionary<PlayerID, byte> _effectiveTiers = new ();
            readonly HashSet<PlayerID> _knownPlayers = new ();

            public readonly PredictedObjectID root;
            public readonly byte minimumInterestTier;
            public int seenVersion;
            public PlayerID? observedOwner;

            internal RootTarget(PredictionInterestModule module, PredictedObjectID root, byte minimumInterestTier)
            {
                _module = module;
                this.root = root;
                this.minimumInterestTier = minimumInterestTier;
            }

            public Vector3 position
            {
                get
                {
                    if (_module._manager.hierarchy &&
                        _module._manager.hierarchy.TryGetGameObject(root, out var go) && go)
                        return go.transform.position;
                    return default;
                }
            }

            public uint staggerSeed => root.instanceId.value;

            public void ApplyTier(PlayerID player, byte tier)
            {
                _knownPlayers.Add(player);
                _computedTiers[player] = tier;
                ApplyEffectiveTier(player);
            }

            public byte GetTier(PlayerID player)
            {
                if (_effectiveTiers.TryGetValue(player, out var tier))
                    return tier;
                return _module.ResolveTier(this, player, _computedTiers.GetValueOrDefault(player, (byte)0));
            }

            public void AddPlayer(PlayerID player)
            {
                _knownPlayers.Add(player);
                ApplyEffectiveTier(player);
            }

            public void RefreshEffectiveTiers()
            {
                foreach (var player in _knownPlayers)
                    ApplyEffectiveTier(player);
            }

            public void RemovePlayer(PlayerID player)
            {
                _knownPlayers.Remove(player);
                _computedTiers.Remove(player);
                _effectiveTiers.Remove(player);
            }

            public void NotifyRemoved()
            {
                foreach (var pair in _effectiveTiers)
                    _module.AdjustTierCounts(pair.Key, pair.Value, 0);
            }

            private void ApplyEffectiveTier(PlayerID player)
            {
                byte computed = _computedTiers.GetValueOrDefault(player, (byte)0);
                byte next = _module.ResolveTier(this, player, computed);
                byte previous = _effectiveTiers.GetValueOrDefault(player, (byte)0);

                _effectiveTiers[player] = next;

                if (previous != next)
                    _module.HandleServerTierChanged(player, root, previous, next);
            }
        }

        private sealed class AnchorRegistration
        {
            public PlayerID player;
            public int seenVersion;
        }

        private struct PlayerTierCounts
        {
            public int serializationAffecting;
            public int culled;
        }

        private readonly struct PendingLocalTier
        {
            public readonly byte tier;
            public readonly ulong serverTick;

            public PendingLocalTier(byte tier, ulong serverTick)
            {
                this.tier = tier;
                this.serverTick = serverTick;
            }
        }

        private readonly struct QueuedLocalTier
        {
            public readonly PredictedObjectID root;
            public readonly byte tier;
            public readonly ulong serverTick;

            public QueuedLocalTier(PredictedObjectID root, byte tier, ulong serverTick)
            {
                this.root = root;
                this.tier = tier;
                this.serverTick = serverTick;
            }
        }

        readonly PredictionManager _manager;
        readonly PredictionLODProfile _profile;
        readonly NetworkLODFactory _factory;
        readonly NetworkLODModule _lodModule;
        readonly bool _asServer;
        readonly Dictionary<PredictedObjectID, RootTarget> _roots = new ();
        readonly HashSet<PlayerID> _players = new ();
        readonly Dictionary<(PlayerID, PredictedObjectID), PredictionInterestPin> _pins = new ();
        readonly Dictionary<PredictedTransform, AnchorRegistration> _anchors = new ();
        readonly List<PredictedObjectID> _rootScratch = new ();
        readonly List<PredictedTransform> _anchorScratch = new ();
        readonly Dictionary<PredictedObjectID, byte> _localTiers = new ();
        readonly Dictionary<PredictedObjectID, PendingLocalTier> _pendingLocalTiers = new ();
        readonly List<QueuedLocalTier> _queuedLocalTiers = new ();
        readonly Dictionary<PlayerID, PlayerTierCounts> _tierCounts = new ();
        readonly List<(PlayerID, PredictedObjectID)> _pinScratch = new ();
        int _refreshVersion;

        internal PredictionInterestModule(PredictionManager manager, PredictionLODProfile profile,
            NetworkLODFactory factory, NetworkLODModule lodModule, bool asServer)
        {
            _manager = manager;
            _profile = profile;
            _factory = factory;
            _lodModule = lodModule;
            _asServer = asServer;
        }

        /// <summary>
        /// Raised on a client after a root's local tier changes. The arguments are the root,
        /// previous tier, and current tier. Re-entry is reported after absolute state is applied.
        /// </summary>
        public event Action<PredictedObjectID, byte, byte> OnLocalRelevanceChanged;

        /// <summary>
        /// Raised on the server after a root's effective tier changes for a player.
        /// The arguments are the player, root, previous tier, and current tier. This includes
        /// changes between two visible tiers as well as transitions across the culled boundary.
        /// </summary>
        public event Action<PlayerID, PredictedObjectID, byte, byte> OnServerTierChanged;

        /// <summary>
        /// Raised on the server when a root leaves the culled tier for a player.
        /// The arguments are the player and root. Visible-tier changes do not raise this event.
        /// </summary>
        public event Action<PlayerID, PredictedObjectID> OnServerBecameRelevant;

        /// <summary>
        /// Raised on the server when a root enters the culled tier for a player.
        /// The arguments are the player and root. Visible-tier changes do not raise this event.
        /// </summary>
        public event Action<PlayerID, PredictedObjectID> OnServerBecameIrrelevant;

        /// <summary>
        /// Gets or sets the server-side tier provider applied after the prefab tier floor.
        /// </summary>
        public IPredictionInterestProvider provider { get; set; }

        /// <summary>
        /// Gets or sets the server-side send scheduler. A null value uses the interval scheduler.
        /// </summary>
        public ILODScheduler scheduler { get; set; }

        /// <summary>
        /// Gets the prediction LOD profile used by this module.
        /// </summary>
        public PredictionLODProfile profile => _profile;

        /// <summary>
        /// Gets whether this module has an active network LOD profile.
        /// </summary>
        public bool enabled => _profile && _profile.networkProfile;

        /// <summary>
        /// Sets or clears the interest override for one player and root.
        /// </summary>
        /// <returns>True when the stored pin changed.</returns>
        public bool SetPin(PlayerID player, PredictedObjectID root, PredictionInterestPin pin)
        {
            var key = (player, root);
            bool changed;

            if (pin == PredictionInterestPin.Default)
                changed = _pins.Remove(key);
            else if (_pins.TryGetValue(key, out var previous))
            {
                changed = previous != pin;
                _pins[key] = pin;
            }
            else
            {
                _pins.Add(key, pin);
                changed = true;
            }

            if (changed && _roots.TryGetValue(root, out var target))
            {
                target.AddPlayer(player);
                target.RefreshEffectiveTiers();
            }

            return changed;
        }

        /// <summary>
        /// Gets the server's effective tier for one player and root.
        /// </summary>
        public bool TryGetTier(PlayerID player, PredictedObjectID root, out byte tier)
        {
            if (root.instanceId.value == 1)
            {
                tier = 0;
                return true;
            }

            if (_roots.TryGetValue(root, out var target))
            {
                tier = target.GetTier(player);
                return true;
            }

            tier = 0;
            return false;
        }

        /// <summary>
        /// Gets a client's received nonzero tier for a root. Tier zero is implicit when false.
        /// </summary>
        public bool TryGetLocalRelevance(PredictedObjectID root, out byte tier)
        {
            if (_localTiers.TryGetValue(root, out tier))
                return true;
            tier = 0;
            return false;
        }

        internal bool IsRelevant(PlayerID player, PredictedObjectID root)
        {
            return !enabled || !TryGetTier(player, root, out var tier) || tier != NetworkLODProfile.CulledTier;
        }

        internal bool ShouldSerializeState(PlayerID player, PredictedObjectID root, ulong tick, bool force)
        {
            if (!enabled || !_roots.TryGetValue(root, out var target))
                return true;

            byte tier = target.GetTier(player);
            if (tier == NetworkLODProfile.CulledTier)
                return false;
            if (force)
                return true;

            var activeScheduler = scheduler ?? LODIntervalScheduler.instance;
            return activeScheduler.ShouldSendThisTick(
                target,
                _profile.networkProfile,
                player,
                tier,
                unchecked((uint)tick));
        }

        internal bool IsLocallyRelevant(PredictedObjectID root)
        {
            return !_localTiers.TryGetValue(root, out var tier) || tier != NetworkLODProfile.CulledTier;
        }

        internal int GetLocalSendIntervalTicks(PredictedObjectID root)
        {
            if (!enabled)
                return 1;

            byte tier = _localTiers.GetValueOrDefault(root, (byte)0);
            return _profile.networkProfile.GetSendIntervalTicks(tier);
        }

        internal PredictionPolicy? GetLocalSuggestedPolicy(PredictedObjectID root)
        {
            if (!enabled)
                return null;

            byte tier = _localTiers.GetValueOrDefault(root, (byte)0);
            return _profile.TryGetSuggestedPolicy(tier, out var policy) ? policy : null;
        }

        internal bool CanApplyLocalAbsolute(PredictedObjectID root, ulong serverTick)
        {
            if (IsLocallyRelevant(root))
                return true;
            return _pendingLocalTiers.TryGetValue(root, out var pending) && pending.serverTick <= serverTick;
        }

        internal bool CanApplyLocalInput(PredictedObjectID root, ulong inputTick)
        {
            if (IsLocallyRelevant(root))
                return true;
            return _pendingLocalTiers.TryGetValue(root, out var pending) && pending.serverTick <= inputTick;
        }

        internal bool CanMaterializeLocalRoot(PredictedObjectID root, ulong serverTick)
        {
            return !IsLocallyRelevant(root) &&
                   _pendingLocalTiers.TryGetValue(root, out var pending) &&
                   pending.serverTick <= serverTick;
        }

        internal bool HasPendingLocalAbsolute(PredictedObjectID root, ulong serverTick)
        {
            return _pendingLocalTiers.TryGetValue(root, out var pending) && pending.serverTick <= serverTick;
        }

        internal bool IsIntentionallyUnmaterialized(PredictedObjectID root)
        {
            return !IsLocallyRelevant(root);
        }

        internal bool HasCulledRoots(PlayerID player)
        {
            return enabled && _tierCounts.TryGetValue(player, out var counts) && counts.culled > 0;
        }

        internal bool HasSerializationAffectingRoots(PlayerID player)
        {
            return enabled && _tierCounts.TryGetValue(player, out var counts) &&
                   counts.serializationAffecting > 0;
        }

        private void AdjustTierCounts(PlayerID player, byte previous, byte next)
        {
            var counts = _tierCounts.GetValueOrDefault(player);
            if (previous != 0)
                counts.serializationAffecting--;
            if (next != 0)
                counts.serializationAffecting++;
            if (previous == NetworkLODProfile.CulledTier)
                counts.culled--;
            if (next == NetworkLODProfile.CulledTier)
                counts.culled++;

            if (counts.serializationAffecting > 0 || counts.culled > 0)
                _tierCounts[player] = counts;
            else
                _tierCounts.Remove(player);
        }

        internal void RefreshServerTargets(List<PredictedIdentity> systems, int systemCount)
        {
            if (!_asServer || !enabled || _lodModule == null)
                return;

            _refreshVersion++;

            for (var i = 0; i < systemCount; i++)
            {
                var system = systems[i];
                var root = system.rootObjectId;

                if (root.instanceId.value != 1)
                {
                    if (!_roots.TryGetValue(root, out var target))
                    {
                        target = new RootTarget(this, root, ResolveRootMinimumInterestTier(root));
                        _roots.Add(root, target);
                        _lodModule.Register(target, _profile.networkProfile);

                        foreach (var player in _players)
                            target.AddPlayer(player);
                    }

                    if (target.seenVersion != _refreshVersion)
                    {
                        target.seenVersion = _refreshVersion;
                        target.observedOwner = null;
                    }

                    if (system.owner.HasValue)
                        target.observedOwner = system.owner;
                }

                if (system is PredictedTransform predictedTransform && system.owner.HasValue &&
                    _players.Contains(system.owner.Value))
                    RefreshAnchor(predictedTransform, system.owner.Value);
            }

            _rootScratch.Clear();
            foreach (var pair in _roots)
            {
                if (pair.Value.seenVersion != _refreshVersion)
                {
                    _rootScratch.Add(pair.Key);
                    continue;
                }

                pair.Value.RefreshEffectiveTiers();
            }

            for (var i = 0; i < _rootScratch.Count; i++)
            {
                var root = _rootScratch[i];
                var target = _roots[root];
                target.NotifyRemoved();
                _lodModule.Unregister(target);
                _roots.Remove(root);
                _manager.RemoveInterestRoot(root);
            }

            if (_rootScratch.Count > 0 && _pins.Count > 0)
            {
                _pinScratch.Clear();
                foreach (var pair in _pins)
                {
                    if (_rootScratch.Contains(pair.Key.Item2))
                        _pinScratch.Add(pair.Key);
                }

                for (var i = 0; i < _pinScratch.Count; i++)
                    _pins.Remove(_pinScratch[i]);
            }

            _anchorScratch.Clear();
            foreach (var pair in _anchors)
            {
                if (pair.Value.seenVersion != _refreshVersion)
                    _anchorScratch.Add(pair.Key);
            }

            for (var i = 0; i < _anchorScratch.Count; i++)
            {
                var anchor = _anchorScratch[i];
                var registration = _anchors[anchor];
                if (anchor)
                    _factory.UnregisterAnchor(registration.player, anchor.transform);
                _anchors.Remove(anchor);
            }
        }

        internal void InitializePlayer(PlayerID player)
        {
            _players.Add(player);

            foreach (var target in _roots.Values)
            {
                target.AddPlayer(player);
                byte tier = target.GetTier(player);
                if (tier != 0)
                    _manager.QueueInterestTierChange(player, target.root, tier);
            }
        }

        internal void RemovePlayer(PlayerID player)
        {
            _players.Remove(player);
            _tierCounts.Remove(player);

            foreach (var target in _roots.Values)
                target.RemovePlayer(player);

            _anchorScratch.Clear();
            foreach (var pair in _anchors)
            {
                if (pair.Value.player == player)
                    _anchorScratch.Add(pair.Key);
            }

            for (var i = 0; i < _anchorScratch.Count; i++)
            {
                var anchor = _anchorScratch[i];
                if (anchor)
                    _factory.UnregisterAnchor(player, anchor.transform);
                _anchors.Remove(anchor);
            }

            _rootScratch.Clear();
            foreach (var pair in _pins)
            {
                if (pair.Key.Item1 == player)
                    _rootScratch.Add(pair.Key.Item2);
            }

            for (var i = 0; i < _rootScratch.Count; i++)
                _pins.Remove((player, _rootScratch[i]));
        }

        internal void QueueLocalTier(PredictedObjectID root, byte tier, ulong serverTick)
        {
            int index = _queuedLocalTiers.Count;
            while (index > 0 && _queuedLocalTiers[index - 1].serverTick > serverTick)
                index--;
            _queuedLocalTiers.Insert(index, new QueuedLocalTier(root, tier, serverTick));
        }

        internal void ApplyQueuedLocalTiers(ulong serverTick)
        {
            int count = 0;

            while (count < _queuedLocalTiers.Count && _queuedLocalTiers[count].serverTick <= serverTick)
            {
                var queued = _queuedLocalTiers[count];
                ReceiveLocalTier(queued.root, queued.tier, queued.serverTick);
                count++;
            }

            if (count > 0)
                _queuedLocalTiers.RemoveRange(0, count);
        }

        internal void ReceiveLocalTier(PredictedObjectID root, byte tier, ulong serverTick)
        {
            byte current = _localTiers.GetValueOrDefault(root, (byte)0);

            if (current == NetworkLODProfile.CulledTier && tier != NetworkLODProfile.CulledTier)
            {
                _pendingLocalTiers[root] = new PendingLocalTier(tier, serverTick);
                return;
            }

            _pendingLocalTiers.Remove(root);
            ApplyLocalTier(root, tier);
        }

        internal bool ConfirmLocalAbsolute(PredictedObjectID root, ulong serverTick)
        {
            if (!_pendingLocalTiers.TryGetValue(root, out var pending) || pending.serverTick > serverTick)
                return false;

            _pendingLocalTiers.Remove(root);
            ApplyLocalTier(root, pending.tier);
            return true;
        }

        internal void PruneStaleLocalTiers()
        {
            if (_localTiers.Count == 0 && _pendingLocalTiers.Count == 0)
                return;

            var hierarchy = _manager.hierarchy;
            if (!hierarchy)
                return;

            uint spawnedThrough = hierarchy.currentState.nextInstanceId;

            _rootScratch.Clear();
            foreach (var pair in _localTiers)
            {
                if (pair.Key.instanceId.value < spawnedThrough && !hierarchy.HasRootRecords(pair.Key))
                    _rootScratch.Add(pair.Key);
            }

            for (var i = 0; i < _rootScratch.Count; i++)
                _localTiers.Remove(_rootScratch[i]);

            _rootScratch.Clear();
            foreach (var pair in _pendingLocalTiers)
            {
                if (pair.Key.instanceId.value < spawnedThrough && !hierarchy.HasRootRecords(pair.Key))
                    _rootScratch.Add(pair.Key);
            }

            for (var i = 0; i < _rootScratch.Count; i++)
                _pendingLocalTiers.Remove(_rootScratch[i]);
        }

        private void RefreshAnchor(PredictedTransform anchor, PlayerID player)
        {
            if (_anchors.TryGetValue(anchor, out var registration))
            {
                if (registration.player != player)
                {
                    _factory.UnregisterAnchor(registration.player, anchor.transform);
                    _factory.RegisterAnchor(player, anchor.transform);
                    registration.player = player;
                }

                registration.seenVersion = _refreshVersion;
                return;
            }

            _factory.RegisterAnchor(player, anchor.transform);
            _anchors.Add(anchor, new AnchorRegistration
            {
                player = player,
                seenVersion = _refreshVersion
            });
        }

        internal byte ResolveTier(RootTarget target, PlayerID player, byte computedTier)
        {
            byte tier = computedTier >= target.minimumInterestTier
                ? computedTier
                : target.minimumInterestTier;
            tier = provider?.ResolveTier(_manager, player, target.root, tier) ?? tier;

            if (_pins.TryGetValue((player, target.root), out var pin))
            {
                if (pin == PredictionInterestPin.Relevant)
                    tier = 0;
                else if (pin == PredictionInterestPin.Culled)
                    tier = NetworkLODProfile.CulledTier;
            }

            if (target.observedOwner == player)
                tier = 0;

            return tier;
        }

        private byte ResolveRootMinimumInterestTier(PredictedObjectID root)
        {
            var prefabs = _manager.predictedPrefabs;
            var hierarchy = _manager.hierarchy;
            if (!prefabs || !hierarchy)
                return 0;

            var records = hierarchy.currentState.spawnedPrefabs;
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (!record.isRootRecord || !record.instanceId.Equals(root))
                    continue;

                int prefabId = record.prefabId;
                if (prefabId < 0 || prefabId >= prefabs.prefabs.Count)
                    return 0;

                return ClampMinimumInterestTier(prefabs.prefabs[prefabId].minimumInterestTier);
            }

            return 0;
        }

        internal byte ClampMinimumInterestTier(byte tier)
        {
            if (tier == NetworkLODProfile.CulledTier)
                return tier;

            int tierCount = _profile && _profile.networkProfile
                ? _profile.networkProfile.tierCount
                : 0;
            if (tierCount <= 0)
                return 0;

            return (byte)Mathf.Min(tier, tierCount - 1);
        }

        private void HandleServerTierChanged(PlayerID player, PredictedObjectID root, byte previous, byte next)
        {
            AdjustTierCounts(player, previous, next);

            _manager.QueueInterestTierChange(player, root, next);
            OnServerTierChanged?.Invoke(player, root, previous, next);

            bool wasRelevant = previous != NetworkLODProfile.CulledTier;
            bool isRelevant = next != NetworkLODProfile.CulledTier;
            if (wasRelevant == isRelevant)
                return;

            if (isRelevant)
                OnServerBecameRelevant?.Invoke(player, root);
            else
                OnServerBecameIrrelevant?.Invoke(player, root);
        }

        private void ApplyLocalTier(PredictedObjectID root, byte tier)
        {
            byte previous = _localTiers.GetValueOrDefault(root, (byte)0);
            if (previous == tier)
            {
                if (tier == NetworkLODProfile.CulledTier)
                    _manager.DematerializeLocalInterestRoot(root);
                return;
            }

            if (tier == 0)
                _localTiers.Remove(root);
            else
                _localTiers[root] = tier;

            _manager.HandleLocalInterestChanged(root);
            if (tier == NetworkLODProfile.CulledTier)
                _manager.DematerializeLocalInterestRoot(root);
            OnLocalRelevanceChanged?.Invoke(root, previous, tier);
        }

        /// <summary>
        /// Releases interest registrations and restores locally culled roots.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        internal void Dispose(bool restoreLocalRoots)
        {
            _rootScratch.Clear();
            foreach (var pair in _localTiers)
                _rootScratch.Add(pair.Key);

            for (var i = 0; i < _rootScratch.Count; i++)
            {
                var root = _rootScratch[i];
                _localTiers.Remove(root);
                _pendingLocalTiers.Remove(root);
                if (restoreLocalRoots)
                {
                    _manager.MaterializeLocalInterestRoot(root);
                    _manager.HandleLocalInterestChanged(root);
                }
            }

            if (_lodModule != null)
            {
                foreach (var target in _roots.Values)
                    _lodModule.Unregister(target);
            }

            if (_factory != null)
            {
                foreach (var pair in _anchors)
                {
                    if (pair.Key)
                        _factory.UnregisterAnchor(pair.Value.player, pair.Key.transform);
                }
            }

            _roots.Clear();
            _players.Clear();
            _pins.Clear();
            _anchors.Clear();
            _localTiers.Clear();
            _pendingLocalTiers.Clear();
            _queuedLocalTiers.Clear();
            _tierCounts.Clear();
        }
    }
}
