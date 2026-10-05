using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        internal const int ViewOffsetBits = 10;

        internal const double ViewOffsetQuantum = 1d / 64d;

        internal const uint ViewOffsetMaxQuantized = (1u << ViewOffsetBits) - 1;

        internal const double DefaultViewOffsetTicks = 0.5d;

        private struct ViewOffsetSample : IDisposable
        {
            public uint quantized;

            public void Dispose() { }
        }

        internal struct ColliderTickSample : IDisposable
        {
            public uint tickManagerTick;

            public void Dispose() { }
        }

        internal readonly struct PlayerViewOffset
        {
            public readonly PlayerID player;
            public readonly uint quantized;

            public PlayerViewOffset(PlayerID player, uint quantized)
            {
                this.player = player;
                this.quantized = quantized;
            }
        }

        private readonly Dictionary<PlayerID, History<ViewOffsetSample>> _viewOffsets = new();
        private readonly List<PlayerID> _viewOffsetPlayerScratch = new();
        private History<ColliderTickSample> _colliderTickMap;
        private PredictionViewClock _viewClock;
        private PredictedLagCompensation _lagCompensation;

        /// <summary>
        /// Physics queries against PurrNet's collider rollback history, addressed in prediction ticks.
        /// Requires a <see cref="ColliderRollback"/> component on the objects that should be hittable.
        /// </summary>
        public PredictedLagCompensation lagCompensation => _lagCompensation ??= new PredictedLagCompensation(this);

        /// <summary>
        /// Longest rewind a client may request, in ticks. Offsets above this are clamped on the server.
        /// </summary>
        public double maxLagCompensationTicks => Math.Max(0d, _maxLagCompensationSeconds) * Math.Max(1, tickRate);

        /// <summary>
        /// The precise prediction tick this client is presenting, including the fraction between two
        /// ticks. Follows the view interpolation buffer, so it lags <see cref="localTick"/> by the
        /// buffered depth. A server without a local view reports <see cref="localTick"/>.
        /// </summary>
        public double viewTick => _viewClock?.viewTick ?? localTick;

        /// <summary>
        /// Newest server tick whose authoritative frame this client has applied. Equals
        /// <see cref="localTick"/> on the server.
        /// </summary>
        public ulong verifiedServerTick => cachedIsServer ? localTick : _verifiedServerTick;

        /// <summary>
        /// How many ticks the predicted head runs ahead of <see cref="verifiedServerTick"/>.
        /// </summary>
        public ulong lead
        {
            get
            {
                var verified = verifiedServerTick;
                return localTick > verified ? localTick - verified : 0;
            }
        }

        /// <summary>
        /// The precise prediction tick <paramref name="player"/> was presenting when it produced the
        /// input for <paramref name="tick"/>. Rewind hit tests to this tick to see the world as that
        /// player saw it. Server-controlled inputs (no player, or a bot) return <paramref name="tick"/>.
        /// Players whose offset is unknown fall back to their last known offset, then to the local
        /// player's, then to <see cref="DefaultViewOffsetTicks"/>.
        /// </summary>
        public double GetLagCompensationTick(PlayerID? player, ulong tick)
        {
            if (!player.HasValue || player.Value.isBot)
                return tick;

            double offset = ResolveViewOffsetTicks(player.Value, tick);
            return offset >= tick ? 0d : tick - offset;
        }

        /// <summary>
        /// Converts a prediction tick into the precise tick PurrNet's <see cref="Modules.RollbackModule"/>
        /// records against. The two counters drift apart on lead adjustments and hitches, so the
        /// mapping is recorded every simulated tick rather than derived from a constant offset.
        /// </summary>
        public bool TryGetColliderRollbackTick(double predictionTick, out double colliderRollbackTick)
        {
            return ResolveColliderRollbackTick(_colliderTickMap, predictionTick, out colliderRollbackTick);
        }

        internal static uint QuantizeViewOffset(double offsetTicks)
        {
            if (double.IsNaN(offsetTicks) || offsetTicks <= 0d)
                return 0;

            double quantized = Math.Round(offsetTicks / ViewOffsetQuantum);
            return quantized >= ViewOffsetMaxQuantized ? ViewOffsetMaxQuantized : (uint)quantized;
        }

        internal static double DequantizeViewOffset(uint quantized) => quantized * ViewOffsetQuantum;

        internal static uint ClampViewOffset(uint quantized, double maxOffsetTicks)
        {
            uint max = QuantizeViewOffset(maxOffsetTicks);
            return quantized > max ? max : quantized;
        }

        internal static bool ResolveColliderRollbackTick(
            History<ColliderTickSample> map, double predictionTick, out double colliderRollbackTick)
        {
            colliderRollbackTick = 0;
            if (map == null || map.Count == 0 || double.IsNaN(predictionTick) || predictionTick < 0)
                return false;

            double floor = Math.Floor(predictionTick);
            double fraction = predictionTick - floor;
            ulong tick = (ulong)floor;

            if (map.Find(tick, out int index))
            {
                colliderRollbackTick = map[index].tickManagerTick + fraction;
                return true;
            }

            if (index > 0 && index < map.Count)
            {
                ulong previousTick = map.GetEntryTick(index - 1);
                ulong nextTick = map.GetEntryTick(index);
                double previousRollback = map[index - 1].tickManagerTick;
                double nextRollback = map[index].tickManagerTick;
                double t = (predictionTick - previousTick) / (double)(nextTick - previousTick);
                colliderRollbackTick = previousRollback + (nextRollback - previousRollback) * t;
                return true;
            }

            if (index > 0)
            {
                ulong entryTick = map.GetEntryTick(index - 1);
                colliderRollbackTick = map[index - 1].tickManagerTick + (double)(tick - entryTick) + fraction;
                return true;
            }

            ulong firstTick = map.GetEntryTick(index);
            colliderRollbackTick = map[index].tickManagerTick - (double)(firstTick - tick) + fraction;
            return true;
        }

        private int lagCompensationHistoryCapacity => Math.Max(64, Math.Max(1, tickRate) * 2);

        private History<ViewOffsetSample> GetViewOffsetHistory(PlayerID player, bool create)
        {
            if (_viewOffsets.TryGetValue(player, out var history))
                return history;
            if (!create)
                return null;
            history = new History<ViewOffsetSample>(lagCompensationHistoryCapacity);
            _viewOffsets[player] = history;
            return history;
        }

        internal void RecordViewOffset(PlayerID player, ulong tick, uint quantized)
        {
            GetViewOffsetHistory(player, true).Write(tick, new ViewOffsetSample { quantized = quantized });
        }

        internal bool TryGetViewOffset(PlayerID player, ulong tick, out uint quantized)
        {
            var history = GetViewOffsetHistory(player, false);
            if (history != null && history.TryGet(tick, out var sample))
            {
                quantized = sample.quantized;
                return true;
            }

            quantized = 0;
            return false;
        }

        private double ResolveViewOffsetTicks(PlayerID player, ulong tick)
        {
            var history = GetViewOffsetHistory(player, false);
            if (history != null && history.ReadOrPrevious(tick, out var sample))
                return DequantizeViewOffset(sample.quantized);

            var local = localPlayer;
            if (local.HasValue && local.Value != player)
            {
                var ownHistory = GetViewOffsetHistory(local.Value, false);
                if (ownHistory != null && ownHistory.ReadOrPrevious(tick, out var ownSample))
                    return DequantizeViewOffset(ownSample.quantized);
            }

            return DefaultViewOffsetTicks;
        }

        private void RecordLocalViewOffset(ulong tick)
        {
            double presented = _viewClock?.viewTick ?? tick;
            double offset = tick - presented;
            RecordViewOffset(localPlayer ?? default, tick, QuantizeViewOffset(offset));
        }

        private uint GetUploadViewOffset(ulong tick)
        {
            return TryGetViewOffset(localPlayer ?? default, tick, out var quantized) ? quantized : 0;
        }

        private void RecordColliderTick(ulong predictionTick)
        {
            if (_tickManager == null)
                return;

            _colliderTickMap ??= new History<ColliderTickSample>(Math.Max(64, Math.Max(1, tickRate) * 6));
            _colliderTickMap.Write(predictionTick, new ColliderTickSample { tickManagerTick = _tickManager.localTick });
        }

        private void CollectViewOffsets(ulong tick, List<PlayerViewOffset> into)
        {
            into.Clear();
            if (!_forwardViewOffsets)
                return;

            _viewOffsetPlayerScratch.Clear();
            foreach (var pair in _viewOffsets)
            {
                if (pair.Value.TryGet(tick, out _))
                    _viewOffsetPlayerScratch.Add(pair.Key);
            }

            _viewOffsetPlayerScratch.Sort(static (a, b) => a.id.value.CompareTo(b.id.value));
            for (var i = 0; i < _viewOffsetPlayerScratch.Count; i++)
            {
                var player = _viewOffsetPlayerScratch[i];
                _viewOffsets[player].TryGet(tick, out var sample);
                into.Add(new PlayerViewOffset(player, sample.quantized));
            }
        }

        private PredictionViewClock EnsureViewClock()
        {
            if (_viewClock == null || _viewClock.tickRate != tickRate)
                _viewClock = new PredictionViewClock(Math.Max(1, tickRate), localTick);
            return _viewClock;
        }

        private void LatchViewClock()
        {
            if (!isClient)
                return;
            EnsureViewClock().Latch(localTick, refreshViewLatchOnly);
        }

        private void AdvanceViewClock(float deltaTime)
        {
            var clock = EnsureViewClock();
            clock.Advance(deltaTime);
            if (clock.lastAdvanceTrimmed)
                ReportViewBufferTrim();
            if (clock.lastAdvanceStarved)
                ReportViewBufferStarved();
        }

        private void ResetLagCompensation()
        {
            _viewOffsets.Clear();
            _colliderTickMap?.Clear();
            _viewClock = null;
        }
    }
}
