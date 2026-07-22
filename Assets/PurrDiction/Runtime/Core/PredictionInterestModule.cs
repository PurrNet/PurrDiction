using System;
using System.Collections.Generic;
using PurrNet.Modules;
using UnityEngine;

namespace PurrNet.Prediction
{
    public enum PredictionInterestPin : byte
    {
        Default,
        Relevant,
        Culled
    }

    public interface IPredictionInterestProvider
    {
        byte ResolveTier(PredictionManager manager, PlayerID player, PredictedObjectID root, byte computedTier);
    }

    public sealed class PredictionInterestModule : IDisposable
    {
        private sealed class RootTarget : ILODTarget
        {
            readonly PredictionInterestModule _module;
            readonly Dictionary<PlayerID, byte> _computedTiers = new ();
            readonly Dictionary<PlayerID, byte> _effectiveTiers = new ();
            readonly HashSet<PlayerID> _knownPlayers = new ();

            public readonly PredictedObjectID root;
            public int seenVersion;
            public PlayerID? observedOwner;

            public RootTarget(PredictionInterestModule module, PredictedObjectID root)
            {
                _module = module;
                this.root = root;
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
                    _module.OnServerTierChanged(player, root, previous, next);
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

        public event Action<PredictedObjectID, byte, byte> OnLocalRelevanceChanged;

        public IPredictionInterestProvider provider { get; set; }

        public ILODScheduler scheduler { get; set; }

        public PredictionLODProfile profile => _profile;

        public bool enabled => _profile && _profile.networkProfile;

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
                        target = new RootTarget(this, root);
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

        internal void ConfirmLocalAbsolute(PredictedObjectID root, ulong serverTick)
        {
            if (!_pendingLocalTiers.TryGetValue(root, out var pending) || pending.serverTick > serverTick)
                return;

            _pendingLocalTiers.Remove(root);
            ApplyLocalTier(root, pending.tier);
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
                if (pair.Key.instanceId.value < spawnedThrough && !hierarchy.TryGetGameObject(pair.Key, out _))
                    _rootScratch.Add(pair.Key);
            }

            for (var i = 0; i < _rootScratch.Count; i++)
                _localTiers.Remove(_rootScratch[i]);

            _rootScratch.Clear();
            foreach (var pair in _pendingLocalTiers)
            {
                if (pair.Key.instanceId.value < spawnedThrough && !hierarchy.TryGetGameObject(pair.Key, out _))
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

        private byte ResolveTier(RootTarget target, PlayerID player, byte computedTier)
        {
            byte tier = provider?.ResolveTier(_manager, player, target.root, computedTier) ?? computedTier;

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

        private void OnServerTierChanged(PlayerID player, PredictedObjectID root, byte previous, byte next)
        {
            AdjustTierCounts(player, previous, next);

            _manager.QueueInterestTierChange(player, root, next);
        }

        private void ApplyLocalTier(PredictedObjectID root, byte tier)
        {
            byte previous = _localTiers.GetValueOrDefault(root, (byte)0);
            if (previous == tier)
                return;

            if (tier == 0)
                _localTiers.Remove(root);
            else
                _localTiers[root] = tier;

            _manager.HandleLocalInterestChanged(root);
            OnLocalRelevanceChanged?.Invoke(root, previous, tier);
        }

        public void Dispose()
        {
            _rootScratch.Clear();
            foreach (var pair in _localTiers)
                _rootScratch.Add(pair.Key);

            for (var i = 0; i < _rootScratch.Count; i++)
            {
                var root = _rootScratch[i];
                _localTiers.Remove(root);
                _manager.HandleLocalInterestChanged(root);
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
            _tierCounts.Clear();
        }
    }
}
