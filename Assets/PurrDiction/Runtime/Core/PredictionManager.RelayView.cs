using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        /// <summary>
        /// Local ticks of view-lag history the relayed view offset is sized against.
        /// </summary>
        internal const int RelayViewLagWindow = 64;

        /// <summary>
        /// Largest share by which a relayed view may run fast or slow while its offset settles
        /// toward the recent lag. Small enough to read as steady motion.
        /// </summary>
        internal const double RelayViewOffsetGlideRate = 0.05d;

        /// <summary>
        /// Excess over the target the offset glides away; a larger excess (left by a stall)
        /// snaps, since gliding it off would keep relayed objects seconds behind.
        /// </summary>
        internal const double RelayViewOffsetSnapSeconds = 0.1d;

        private readonly double[] _relayViewLags = new double[RelayViewLagWindow];
        private int _relayViewLagCursor;
        private ulong _relayViewLagTick;
        private double _relayViewOffset;
        private bool _hasRelayViewOffset;

        // A relayed identity on a client does not simulate: its state is the newest verified one.
        // Stamping that state with the local tick it was latched on ties the view to when frames
        // happened to arrive, so a burst of two frames then a gap moves it two ticks, then none.
        // Relayed samples are instead stamped with the server tick they show and presented at
        // relayViewTick, a steady distance behind the view clock, so they move one tick per tick.

        /// <summary>
        /// Ticks the relayed timeline is presented behind <see cref="viewTick"/>. Follows the
        /// largest recent distance between the view clock and the newest verified tick, so
        /// verified frames arrive before the view reaches them. Diagnostic only.
        /// </summary>
        public double relayViewOffset => _relayViewOffset;

        /// <summary>
        /// Server tick relayed identities present on this client, including the fraction.
        /// </summary>
        internal double relayViewTick => viewTick - _relayViewOffset;

        // Runs once per render frame, after the view clock advanced and before identities sample.
        private void AdvanceRelayViewOffset(float deltaTime)
        {
            if (cachedIsServer || _verifiedServerTick == 0)
                return;

            // How far the view clock is past the newest relayed state. Presenting relayed states
            // further back than this keeps the sample being lerped toward already received.
            double lag = Math.Max(0d, viewTick - RelayedStateTick(_verifiedServerTick));

            if (localTick != _relayViewLagTick)
            {
                _relayViewLagTick = localTick;
                _relayViewLagCursor = _relayViewLagCursor + 1 == RelayViewLagWindow ? 0 : _relayViewLagCursor + 1;
                _relayViewLags[_relayViewLagCursor] = lag;
            }
            else if (lag > _relayViewLags[_relayViewLagCursor])
            {
                _relayViewLags[_relayViewLagCursor] = lag;
            }

            double windowMax = 0d;
            for (var i = 0; i < RelayViewLagWindow; i++)
                windowMax = Math.Max(windowMax, _relayViewLags[i]);

            int rate = Math.Max(1, tickRate);
            _relayViewOffset = NextRelayViewOffset(_hasRelayViewOffset ? _relayViewOffset : lag, lag, windowMax,
                deltaTime * (double)rate, Math.Max(2d, RelayViewOffsetSnapSeconds * rate));
            _hasRelayViewOffset = true;
        }

        /// <summary>
        /// A verified frame later than the offset allows holds the relayed view where it is (the
        /// offset grows to the lag) rather than presenting a state not received yet. Otherwise the
        /// offset moves toward the largest recent lag by at most
        /// <see cref="RelayViewOffsetGlideRate"/> of the elapsed ticks, so relayed objects speed
        /// up or slow down by that share at most; an excess above <paramref name="snapTicks"/>
        /// is dropped at once.
        /// </summary>
        internal static double NextRelayViewOffset(double offset, double lag, double windowMax, double elapsedTicks,
            double snapTicks)
        {
            if (lag >= offset)
                return lag;

            double target = Math.Max(lag, windowMax);
            if (offset - target > snapTicks)
                return target;

            double step = RelayViewOffsetGlideRate * Math.Max(0d, elapsedTicks);
            if (offset > target)
                return Math.Max(target, offset - step);
            if (offset < target)
                return Math.Min(target, offset + step);
            return offset;
        }

        // Applying the verified frame for server tick S simulates S, so a relayed identity then
        // holds the state entering S + 1: the tick a predicted latch of that state would carry.
        private static ulong RelayedStateTick(ulong verifiedTick) => verifiedTick + 1;

        /// <summary>
        /// Stamp for a relayed identity's view sample: the tick of the verified state it shows,
        /// on the same convention as predicted samples. Returns false when no verified state has
        /// arrived since
        /// <paramref name="lastVerifiedTick"/>: the identity has nothing new to show, and another
        /// sample of the same state would only hold the view in place.
        /// </summary>
        internal bool TryStampRelayedView(ref ulong lastVerifiedTick, out ulong stamp)
        {
            ulong verified = _verifiedServerTick;
            if (cachedIsServer || verified == 0 || verified == lastVerifiedTick)
            {
                stamp = 0;
                return false;
            }

            lastVerifiedTick = verified;
            stamp = RelayedStateTick(verified);
            return true;
        }

        private void ResetRelayViewTimeline()
        {
            Array.Clear(_relayViewLags, 0, _relayViewLags.Length);
            _relayViewLagCursor = 0;
            _relayViewLagTick = 0;
            _relayViewOffset = 0;
            _hasRelayViewOffset = false;
        }

        // Lag compensation for relayed objects. A client sees predicted objects at viewTick and
        // relayed ones relayViewOffset ticks earlier, on the server's timeline. A query made for
        // that player rewinds colliders relayed to it further, by that offset: on the server with
        // the offset uploaded with the input, on the client with its own. The client keeps a
        // separate history of its relayed colliders, filled from applied frames and keyed by
        // server tick (CaptureRelayedColliders), so both rewind the same server poses.

        internal const int RelayOffsetBits = 12;
        internal const double RelayOffsetQuantum = 1d / 16d;
        internal const uint RelayOffsetMaxQuantized = (1u << RelayOffsetBits) - 1;

        private History<ViewOffsetSample> _localRelayOffsets;
        private readonly Dictionary<PlayerID, History<ViewOffsetSample>> _relayOffsets = new();
        private PlayerID? _lagCompensationViewer;

        internal static uint QuantizeRelayOffset(double offsetTicks)
        {
            if (double.IsNaN(offsetTicks) || offsetTicks <= 0d)
                return 0;

            double quantized = Math.Round(offsetTicks / RelayOffsetQuantum);
            return quantized >= RelayOffsetMaxQuantized ? RelayOffsetMaxQuantized : (uint)quantized;
        }

        internal static double DequantizeRelayOffset(uint quantized) => quantized * RelayOffsetQuantum;

        // The upload repeats the previous tick's offset with one bit; it changes rarely.
        internal static void WriteRelayOffset(BitPacker packer, uint quantized, ref uint previous, bool first)
        {
            bool same = !first && quantized == previous;
            packer.WriteBits(same ? 1UL : 0UL, 1);
            if (!same)
                packer.WriteBits(quantized, RelayOffsetBits);
            previous = quantized;
        }

        internal static uint ReadRelayOffset(BitPacker packer, ref uint previous, bool first)
        {
            bool same = packer.ReadBits(1) != 0;
            if (same && first)
                throw new InvalidOperationException("The first relay offset of an upload cannot repeat a previous one.");
            if (!same)
                previous = (uint)packer.ReadBits(RelayOffsetBits);
            return previous;
        }

        private void RecordLocalRelayOffset(ulong tick)
            => RecordLocalRelayOffset(tick, QuantizeRelayOffset(_relayViewOffset));

        internal void RecordLocalRelayOffset(ulong tick, uint quantized)
        {
            _localRelayOffsets ??= new History<ViewOffsetSample>(lagCompensationHistoryCapacity);
            _localRelayOffsets.Write(tick, new ViewOffsetSample { quantized = quantized });
        }

        private uint GetUploadRelayOffset(ulong tick)
            => _localRelayOffsets != null && _localRelayOffsets.TryGet(tick, out var sample) ? sample.quantized : 0;

        internal void RecordRelayOffset(PlayerID player, ulong tick, uint quantized)
        {
            if (!_relayOffsets.TryGetValue(player, out var history))
            {
                history = new History<ViewOffsetSample>(lagCompensationHistoryCapacity);
                _relayOffsets[player] = history;
            }
            history.Write(tick, new ViewOffsetSample { quantized = quantized });
        }

        /// <summary>
        /// Ticks by which <paramref name="player"/> presented objects it does not predict
        /// (<see cref="PredictionPolicy.ServerRelay"/>) behind <see cref="GetLagCompensationTick"/>
        /// when it produced the input for <paramref name="tick"/>. The server knows it for every
        /// client; a client knows only its own. Zero for the server's own view, bots and unknown
        /// players. <see cref="lagCompensation"/> queries apply it on their own.
        /// </summary>
        public double GetLagCompensationRelayOffset(PlayerID? player, ulong tick)
        {
            if (!player.HasValue || player.Value.isBot)
                return 0d;

            if (cachedIsServer)
                return Math.Min(GetRelayOffsetTicks(player.Value, tick), maxRelayLagCompensationTicks);

            return player == localPlayer && _localRelayOffsets != null &&
                   _localRelayOffsets.ReadOrPrevious(tick, out var sample)
                ? DequantizeRelayOffset(sample.quantized)
                : 0d;
        }

        internal double GetRelayOffsetTicks(PlayerID player, ulong tick)
        {
            return _relayOffsets.TryGetValue(player, out var history) && history.ReadOrPrevious(tick, out var sample)
                ? DequantizeRelayOffset(sample.quantized)
                : 0d;
        }

        internal struct LagCompensationViewerScope
        {
            internal PlayerID? viewer;
        }

        // Simulate and LateSimulate run inside the scope of the identity's owner, whose screen a
        // lag-compensated query inside them describes.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal LagCompensationViewerScope EnterLagCompensationViewer(PlayerID? viewer)
        {
            var previous = new LagCompensationViewerScope { viewer = _lagCompensationViewer };
            _lagCompensationViewer = viewer;
            return previous;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void ExitLagCompensationViewer(in LagCompensationViewerScope previous)
            => _lagCompensationViewer = previous.viewer;

        /// <summary>
        /// On the server, inside Simulate of an identity owned by a client that presents some
        /// objects relayed: the prediction tick colliders relayed to that client are rewound to
        /// for a query at <paramref name="predictionTick"/>.
        /// </summary>
        internal bool TryGetRelayedLagCompensationTick(double predictionTick, out double relayTick, out PlayerID viewer)
        {
            relayTick = predictionTick;
            viewer = default;
            if (!_lagCompensationViewer.HasValue)
                return false;

            viewer = _lagCompensationViewer.Value;
            if (viewer.isBot)
                return false;

            // A client knows only its own offset; other players' shots it predicts stay unsplit.
            if (!cachedIsServer && viewer != localPlayer)
                return false;

            double offset = GetLagCompensationRelayOffset(viewer, localTickInContext);
            if (offset <= 0d)
                return false;

            relayTick = Math.Max(0d, predictionTick - offset);
            return relayTick < predictionTick;
        }

        private PurrNet.Modules.RollbackModule _relayedRollback;

        /// <summary>
        /// On a client, the collider history of the objects it shows relayed, keyed by server tick
        /// and filled from applied frames. Null on the server, which rewinds its own history.
        /// </summary>
        internal PurrNet.Modules.RollbackModule relayedRollbackModule => _relayedRollback;

        // Applying the frame for server tick S leaves every relayed identity in the state entering
        // S + 1, which is also its pose in the server's collider history for that tick.
        private void CaptureRelayedColliders(ulong stateTick)
        {
            if (cachedIsServer || !isClient || _tickManager == null)
                return;

            _relayedRollback ??= new PurrNet.Modules.RollbackModule(_tickManager, gameObject.scene);

            for (var i = 0; i < _systemsCount; i++)
            {
                var identity = _systems[i];
                identity.SyncRelayedColliderCapture(_relayedRollback, identity.PresentsRelayedView(this), stateTick);
            }
        }

        private void ResetRelayLagCompensation()
        {
            _localRelayOffsets?.Clear();
            _relayOffsets.Clear();
            _lagCompensationViewer = null;
        }
    }
}
