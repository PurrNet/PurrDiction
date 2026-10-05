using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        /// <summary>
        /// Raised on the server when a client's deterministic state diverges from the server's.
        /// Arguments: the diverged identity, the diverged client, the settled tick that mismatched,
        /// and the policy that was applied in response.
        /// </summary>
        public event Action<PredictedIdentity, PlayerID, ulong, DesyncPolicy> onDesyncDetected;

        /// <summary>
        /// Raised on a client when the server notifies it that its deterministic state diverged.
        /// Arguments: the diverged identity (null if it no longer exists locally), the settled tick
        /// that mismatched, and the policy the server applied. Notifications are rate limited to
        /// roughly once per second.
        /// </summary>
        public event Action<PredictedIdentity, ulong, DesyncPolicy> onLocalDesync;

        private ulong _nextDesyncReportTick;

        readonly Dictionary<PlayerID, HashSet<PredictedComponentID>> _pendingDesyncHeals = new ();
        readonly Dictionary<PlayerID, HashSet<PredictedComponentID>> _preparedDesyncHeals = new ();
        readonly Dictionary<PlayerID, Dictionary<PredictedComponentID, ulong>> _desyncHealServedTick = new ();
        readonly Dictionary<PlayerID, ulong> _desyncResyncServedTick = new ();
        readonly Dictionary<PlayerID, ulong> _desyncNoticeCooldownTick = new ();

        private ulong desyncCheckIntervalTicks
        {
            get
            {
                int ticks = Mathf.RoundToInt(_desyncCheckIntervalSeconds * tickRate);
                return ticks < 1 ? 1UL : (ulong)ticks;
            }
        }

        private void MaybeSendDesyncReport(ulong settledTick)
        {
            if (isServer || settledTick < _nextDesyncReportTick)
                return;

            _nextDesyncReportTick = settledTick + desyncCheckIntervalTicks;

            BitPacker payload = null;
            uint count = 0;

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.resolvedDesyncPolicy == DesyncPolicy.Ignore || !system.isDeterministic)
                    continue;
                if (!system.TryGetDeterministicStateHash(settledTick, out var hash))
                    continue;

                payload ??= BitPackerPool.Get();
                Packer<PredictedComponentID>.Write(payload, system.id);
                payload.WriteBits(hash, 16);
                count++;
            }

            if (payload == null)
                return;

            using (payload)
                SendDesyncReportToServer(settledTick, count, payload);
        }

        [ServerRpc(requireOwnership: false, channel: Channel.Unreliable, immediate: true)]
        private void SendDesyncReportToServer(ulong tick, uint count, BitPacker payload, RPCInfo info = default)
        {
            using (payload)
            {
                try
                {
                    HandleDesyncReport(tick, count, payload, info.sender);
                }
                catch
                {
                    // ignored: malformed reports are dropped
                }
            }
        }

        private void HandleDesyncReport(ulong tick, uint count, BitPacker payload, PlayerID sender)
        {
            bool tooOld = tick + (ulong)(tickRate * 5) < localTick;

            for (uint i = 0; i < count; i++)
            {
                PredictedComponentID pid = default;
                Packer<PredictedComponentID>.Read(payload, ref pid);
                ushort clientHash = (ushort)payload.ReadBits(16);

                if (tooOld)
                    continue;

                if (!_instanceMap.TryGetValue(pid, out var system) || !system.isDeterministic)
                    continue;

                var policy = system.resolvedDesyncPolicy;
                if (policy == DesyncPolicy.Ignore)
                    continue;

                if (_desyncResyncServedTick.TryGetValue(sender, out var resyncTick) && tick <= resyncTick)
                    continue;

                if (_desyncHealServedTick.TryGetValue(sender, out var served) &&
                    served.TryGetValue(pid, out var healTick) && tick <= healTick)
                    continue;

                if (!system.TryGetDeterministicStateHash(tick, out var serverHash))
                    continue;

                if (serverHash == clientHash)
                    continue;

                HandleDeterministicDesync(system, sender, tick, policy);
            }
        }

        private void HandleDeterministicDesync(PredictedIdentity system, PlayerID player, ulong tick, DesyncPolicy policy)
        {
            onDesyncDetected?.Invoke(system, player, tick, policy);

            switch (policy)
            {
                case DesyncPolicy.Correct:
                    QueueDesyncHeal(player, system);
                    break;
                case DesyncPolicy.Resync:
                    TriggerDesyncResync(player);
                    break;
            }

            if (_desyncNoticeCooldownTick.TryGetValue(player, out var cooldown) && localTick < cooldown)
                return;

            _desyncNoticeCooldownTick[player] = localTick + (ulong)tickRate;
            PurrLogger.LogWarning(
                $"Deterministic desync of '{system.name}' ({system.id}) for {player} at tick {tick}, applying {policy}.\nServer state:\n{system.GetDeterministicStateString(tick)}",
                system);
            NotifyDesyncToClient(player, system.id, tick, (byte)policy);
        }

        [TargetRpc]
        private void NotifyDesyncToClient([UsedImplicitly] PlayerID player, PredictedComponentID id, ulong tick, byte policy)
        {
            _instanceMap.TryGetValue(id, out var system);

            if (system)
            {
                PurrLogger.LogWarning(
                    $"Local deterministic desync of '{system.name}' ({id}) at tick {tick}, server applied {(DesyncPolicy)policy}.\nLocal state:\n{system.GetDeterministicStateString(tick)}",
                    system);
            }

            onLocalDesync?.Invoke(system, tick, (DesyncPolicy)policy);
        }

        private void QueueDesyncHeal(PlayerID player, PredictedIdentity system)
        {
            if (!_playerVisibility.TryGetValue(player, out var timeline) ||
                !IsSystemVisible(timeline, system))
                return;

            if (!_pendingDesyncHeals.TryGetValue(player, out var pending))
            {
                pending = new HashSet<PredictedComponentID>();
                _pendingDesyncHeals[player] = pending;
            }

            pending.Add(system.id);

            if (!_desyncHealServedTick.TryGetValue(player, out var served))
            {
                served = new Dictionary<PredictedComponentID, ulong>();
                _desyncHealServedTick[player] = served;
            }

            served[system.id] = localTick;
        }

        private void BeginPreparingDesyncHeals(PlayerID player)
        {
            if (_preparedDesyncHeals.TryGetValue(player, out var prepared))
                prepared.Clear();
        }

        private bool PrepareDesyncHeal(PlayerID player, PredictedComponentID id)
        {
            if (!_pendingDesyncHeals.TryGetValue(player, out var pending) || !pending.Contains(id))
                return false;
            if (!_preparedDesyncHeals.TryGetValue(player, out var prepared))
                _preparedDesyncHeals[player] = prepared = new HashSet<PredictedComponentID>();
            prepared.Add(id);
            return true;
        }

        private void CommitPreparedDesyncHeals(PlayerID player)
        {
            if (!_preparedDesyncHeals.TryGetValue(player, out var prepared))
                return;
            if (_pendingDesyncHeals.TryGetValue(player, out var pending))
                pending.ExceptWith(prepared);
            prepared.Clear();
        }

        private void TriggerDesyncResync(PlayerID player)
        {
            if (_desyncResyncServedTick.TryGetValue(player, out var last) &&
                localTick < last + (ulong)tickRate)
                return;

            QueueFullResync(player);
        }

        private bool QueueFullResync(PlayerID player)
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var clientFrame = _clientFrames[i];
                if (!clientFrame.player.Equals(player))
                    continue;

                clientFrame.requiresFullCheckpoint = true;
                clientFrame.fullFrame = false;
                clientFrame.preparedFrameTick = 0;
                clientFrame.preparedVisibilityTick = 0;
                _clientFrames[i] = clientFrame;

                _desyncResyncServedTick[player] = localTick;
                return true;
            }
            return false;
        }

        private void ClearDesyncTrackingForPlayer(PlayerID player)
        {
            _historyResyncServedAt.Remove(player);
            _pendingDesyncHeals.Remove(player);
            _preparedDesyncHeals.Remove(player);
            _desyncHealServedTick.Remove(player);
            _desyncResyncServedTick.Remove(player);
            _desyncNoticeCooldownTick.Remove(player);
        }
    }
}
