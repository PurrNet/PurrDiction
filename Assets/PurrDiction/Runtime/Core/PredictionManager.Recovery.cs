using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private const double HistoryResyncRetrySeconds = 1d;
        private bool _historyResyncPending;
        private ulong _historyResyncRequiredAfterTick;
        private double _nextHistoryResyncRequestAt;
        private ulong _applyingFrameServerTick;
        private readonly Dictionary<PlayerID, double> _historyResyncServedAt = new();

        private void MarkHistoryResyncNeeded(ulong failedTick)
        {
            _awaitingFullFrame = true;
            _historyResyncPending = true;
            _historyResyncRequiredAfterTick = Math.Max(_historyResyncRequiredAfterTick, failedTick);
            SendPendingHistoryResyncRequest();
        }

        private bool TryTakeHistoryResyncRequest(double now)
        {
            if (!_historyResyncPending || now < _nextHistoryResyncRequestAt)
                return false;

            _nextHistoryResyncRequestAt = now + HistoryResyncRetrySeconds;
            return true;
        }

        private void SendPendingHistoryResyncRequest()
        {
            if (!isSpawned || !isClient || isServer ||
                !TryTakeHistoryResyncRequest(Time.unscaledTimeAsDouble))
                return;

            TraceHistoryResync("Request", localPlayer ?? default, _historyResyncRequiredAfterTick);
            RequestHistoryResync(_historyResyncRequiredAfterTick);
        }

        private void CompleteHistoryResync(ulong fullTick)
        {
            if (!_historyResyncPending || fullTick < _historyResyncRequiredAfterTick)
                return;

            _historyResyncPending = false;
            _historyResyncRequiredAfterTick = 0;
            _nextHistoryResyncRequestAt = 0;
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestHistoryResync(ulong failedTick, RPCInfo info = default)
            => HandleHistoryResyncRequest(info.sender, failedTick);

        // Frames are ordered, so a full frame sent after the failed tick is already on its way.
        private void HandleHistoryResyncRequest(PlayerID player, ulong failedTick)
        {
            if (!_clientTicks.ContainsKey(player) || failedTick > localTick)
                return;

            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (!frame.player.Equals(player))
                    continue;

                if (frame.requiresFullCheckpoint || frame.lastFullFrameSentTick > failedTick)
                {
                    TraceHistoryResync("Covered", player, failedTick, frame.lastFullFrameSentTick);
                    return;
                }

                double now = Time.unscaledTimeAsDouble;
                if (_historyResyncServedAt.TryGetValue(player, out double last) &&
                    now - last < HistoryResyncRetrySeconds)
                    return;

                if (QueueFullResync(player))
                {
                    _historyResyncServedAt[player] = now;
                    TraceHistoryResync("Serve", player, failedTick);
                }
                return;
            }
        }
    }
}
