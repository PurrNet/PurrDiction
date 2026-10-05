using System;
using PurrNet.Packing;
using PurrNet.Pooling;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        static readonly ProfilerMarker WritePhysicsEventHistoryMarker =
            new("PredictionManager.WritePhysicsEventHistory");
        static readonly ProfilerMarker ReadPhysicsEventHistoryMarker =
            new("PredictionManager.ReadPhysicsEventHistory");
        private struct HistoricalPhysicsBatch : IDisposable
        {
            public ulong tick;
#if UNITY_PHYSICS_3D
            public PredictedPhysicsData physics3D;
#endif
#if UNITY_PHYSICS_2D
            public PredictedPhysics2DData physics2D;
#endif
            public void Dispose()
            {
#if UNITY_PHYSICS_3D
                physics3D.Dispose();
#endif
#if UNITY_PHYSICS_2D
                physics2D.Dispose();
#endif
            }
        }

        private readonly struct HistoricalPhysicsEvents : IDisposable
        {
            public readonly DisposableList<HistoricalPhysicsBatch> batches;

            public HistoricalPhysicsEvents(DisposableList<HistoricalPhysicsBatch> batches)
                => this.batches = batches;

            public void Dispose()
            {
                if (batches.isDisposed)
                    return;
                for (int i = 0; i < batches.Count; i++)
                    batches[i].Dispose();
                batches.Dispose();
            }
        }

        private void CapturePhysicsEventHierarchy(ulong tick)
        {
            // Capture before callbacks can delete participants, even while recipients await checkpoint ACKs.
            if (hierarchy && (physics3d || physics2d))
                hierarchy.RefreshVerifiedFromLive(tick);
        }

        private void CapturePhysicsEventState(ulong tick)
        {
            TracePhysicsEvents("capture", tick);
#if UNITY_PHYSICS_3D
            if (physics3d)
                physics3d.RefreshVerifiedFromLive(tick);
#endif
#if UNITY_PHYSICS_2D
            if (physics2d)
                physics2d.RefreshVerifiedFromLive(tick);
#endif
        }

        private bool HasPhysicsEventHistory(ulong baselineTick, ulong tick)
        {
            if (!physics3d && !physics2d)
                return true;
            if (baselineTick == 0 || baselineTick >= tick ||
                tick - baselineTick > verifiedHistoryWindowTicks)
                return false;

            for (ulong historicalTick = baselineTick + 1; historicalTick < tick; historicalTick++)
            {
#if UNITY_PHYSICS_3D
                if (physics3d && !physics3d.TryGetExactVerifiedState(historicalTick, out _))
                    return false;
#endif
#if UNITY_PHYSICS_2D
                if (physics2d && !physics2d.TryGetExactVerifiedState(historicalTick, out _))
                    return false;
#endif
            }
            return true;
        }

        private void WritePhysicsEventHistory(PlayerID player, PlayerVisibilityTimeline timeline,
            BitPacker destination, ulong tick, ulong baselineTick, bool fullFrame)
        {
            using var marker = WritePhysicsEventHistoryMarker.Auto();
            // A full frame already includes older callbacks; replaying them would apply their effects twice.
            if (fullFrame || (!physics3d && !physics2d) || tick <= baselineTick + 1)
            {
                Packer<PackedUInt>.Write(destination, 0u);
                return;
            }

            if (!HasPhysicsEventHistory(baselineTick, tick))
                throw new InvalidOperationException("Incomplete physics event history requires a full frame.");

            using var transcript = BitPackerPool.Get();
            using var records = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();
            uint batchCount = 0;
            for (ulong historicalTick = baselineTick + 1; historicalTick < tick; historicalTick++)
            {
                records.ResetPositionAndMode(false);
                int recordCount = 0;
#if UNITY_PHYSICS_3D
                if (physics3d && physics3d.TryGetExactVerifiedState(historicalTick, out var data3D) &&
                    !data3D.events.isDisposed && data3D.events.Count > 0)
                {
                    PredictedPhysicsData projected = default;
                    try
                    {
                        var state = data3D;
                        if (!timeline.isPassThrough)
                        {
                            projected = PredictionPhysicsVisibility.Project(data3D,
                                GetHistoricalHiddenPhysicsPieces(player, timeline, historicalTick));
                            state = projected;
                        }
                        if (state.events.Count > 0)
                        {
                            payload.ResetPositionAndMode(false);
                            Packer<PredictedPhysicsData>.Write(payload, state);
                            AddressedPredictionRecords.WriteRecord(records, physics3d.id, true, payload);
                            recordCount++;
                        }
                    }
                    finally { projected.Dispose(); }
                }
#endif
#if UNITY_PHYSICS_2D
                if (physics2d && physics2d.TryGetExactVerifiedState(historicalTick, out var data2D) &&
                    !data2D.events.isDisposed && data2D.events.Count > 0)
                {
                    PredictedPhysics2DData projected = default;
                    try
                    {
                        var state = data2D;
                        if (!timeline.isPassThrough)
                        {
                            projected = PredictionPhysicsVisibility.Project(data2D,
                                GetHistoricalHiddenPhysicsPieces(player, timeline, historicalTick));
                            state = projected;
                        }
                        if (state.events.Count > 0)
                        {
                            payload.ResetPositionAndMode(false);
                            Packer<PredictedPhysics2DData>.Write(payload, state);
                            AddressedPredictionRecords.WriteRecord(records, physics2d.id, true, payload);
                            recordCount++;
                        }
                    }
                    finally { projected.Dispose(); }
                }
#endif
                if (recordCount == 0)
                    continue;
                Packer<PackedUInt>.Write(transcript, checked((uint)(historicalTick - baselineTick)));
                AddressedPredictionRecords.WriteSectionCount(recordCount, transcript);
                transcript.WriteBitsWithoutConsumingIt(records, records.positionInBits);
                batchCount++;
            }

            Packer<PackedUInt>.Write(destination, batchCount);
            destination.WriteBitsWithoutConsumingIt(transcript, transcript.positionInBits);
        }

        private System.Collections.Generic.HashSet<PredictedObjectID> GetHistoricalHiddenPhysicsPieces(
            PlayerID player, PlayerVisibilityTimeline timeline, ulong tick)
        {
            if (!hierarchy || !hierarchy.TryGetVerifiedState(tick, out _, out var state))
                throw new InvalidOperationException($"Missing physics visibility topology at tick {tick}.");
            return GetHiddenPiecesAt(player, timeline, state, tick);
        }

        private HistoricalPhysicsEvents ReadPhysicsEventHistory(
            BitPacker source, ulong serverTick, ulong baselineTick, bool fullFrame, int frameEndBit)
        {
            using var marker = ReadPhysicsEventHistoryMarker.Auto();
            if (source.positionInBits >= frameEndBit)
                throw new InvalidOperationException("Missing physics event history header.");
            uint count = Packer<PackedUInt>.Read(source);
            if (source.positionInBits > frameEndBit || count > verifiedHistoryWindowTicks ||
                (fullFrame && count != 0))
                throw new InvalidOperationException("Invalid historical physics batch count.");
            if (!fullFrame && (physics3d || physics2d) &&
                (baselineTick == 0 || baselineTick >= serverTick ||
                 serverTick - baselineTick > verifiedHistoryWindowTicks))
                throw new InvalidOperationException("Invalid physics event history window.");
            if (count == 0)
                return default;
            if (baselineTick == 0 || baselineTick >= serverTick ||
                serverTick - baselineTick > verifiedHistoryWindowTicks)
                throw new InvalidOperationException("Invalid physics event history window.");

            var batches = DisposableList<HistoricalPhysicsBatch>.Create(checked((int)count));
            var result = new HistoricalPhysicsEvents(batches);
            try
            {
                ulong previous = baselineTick;
                for (uint i = 0; i < count; i++)
                {
                    uint offset = Packer<PackedUInt>.Read(source);
                    if (source.positionInBits > frameEndBit || offset == 0 || offset >= serverTick - baselineTick ||
                        baselineTick + offset <= previous)
                        throw new InvalidOperationException("Physics event ticks must be ordered inside the delta window.");
                    var batch = new HistoricalPhysicsBatch { tick = baselineTick + offset };
                    previous = batch.tick;
                    // ACK delay can resend already-verified batches; skip their payloads without decoding event lists.
                    if (batch.tick <= _verifiedServerTick)
                    {
                        AddressedPredictionRecords.SkipSection(source, frameEndBit, 2);
                        continue;
                    }
                    var outer = _readingPhysicsBatch;
                    _readingPhysicsBatch = new PhysicsBatchRead { batch = batch };
                    try
                    {
                        int recordsStart = source.positionInBits;
                        AddressedPredictionRecords.SkipSection(source, frameEndBit, 2);
                        source.SetBitPosition(recordsStart);
                        AddressedPredictionRecords.ReadSection(source: source,
                            readRecord: _readPhysicsBatchRecord ??= ReadPhysicsBatchRecord);
                        batch = _readingPhysicsBatch.batch;
                        batches.Add(batch);
                    }
                    catch
                    {
                        _readingPhysicsBatch.batch.Dispose();
                        throw;
                    }
                    finally
                    {
                        _readingPhysicsBatch = outer;
                    }
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private struct PhysicsBatchRead
        {
            public HistoricalPhysicsBatch batch;
            public bool has3D;
            public bool has2D;
        }

        private PhysicsBatchRead _readingPhysicsBatch;
        private AddressedPredictionRecords.ReadRecord _readPhysicsBatchRecord;

        private void ReadPhysicsBatchRecord(PredictedComponentID id, bool full, BitPacker payload, int payloadBitCount)
        {
            if (!full)
                throw new InvalidOperationException("Historical physics batches must be self-contained.");
#if UNITY_PHYSICS_3D
            if (physics3d && id.Equals(physics3d.id))
            {
                if (_readingPhysicsBatch.has3D)
                    throw new InvalidOperationException("Duplicate historical 3D physics batch.");
                _readingPhysicsBatch.has3D = true;
                Packer<PredictedPhysicsData>.Read(payload, ref _readingPhysicsBatch.batch.physics3D);
                return;
            }
#endif
#if UNITY_PHYSICS_2D
            if (physics2d && id.Equals(physics2d.id))
            {
                if (_readingPhysicsBatch.has2D)
                    throw new InvalidOperationException("Duplicate historical 2D physics batch.");
                _readingPhysicsBatch.has2D = true;
                Packer<PredictedPhysics2DData>.Read(payload, ref _readingPhysicsBatch.batch.physics2D);
                return;
            }
#endif
            throw new InvalidOperationException($"Unknown historical physics handler {id}.");
        }

        private void ApplyPhysicsEventHistory(HistoricalPhysicsEvents history, ulong tick, ref int index)
        {
            HistoricalPhysicsBatch batch = default;
            if (!history.batches.isDisposed)
            {
                while (index < history.batches.Count && history.batches[index].tick < tick)
                    index++;
                if (index < history.batches.Count && history.batches[index].tick == tick)
                    batch = history.batches[index++];
            }

            // Replace even silent ticks; rollback may have restored older events that must not fire again.
#if UNITY_PHYSICS_3D
            if (physics3d)
            {
                ref var events = ref physics3d.currentState.events;
                if (events.isDisposed)
                    events = DisposableList<PhysicsEvent>.Create(0);
                for (int i = 0; i < events.Count; i++)
                    events[i].Dispose();
                events.Clear();
                if (!batch.physics3D.events.isDisposed)
                    for (int i = 0; i < batch.physics3D.events.Count; i++)
                        events.Add(batch.physics3D.events[i].Duplicate());
            }
#endif
#if UNITY_PHYSICS_2D
            if (physics2d)
            {
                ref var events = ref physics2d.currentState.events;
                if (events.isDisposed)
                    events = DisposableList<Physics2DEvent>.Create(0);
                for (int i = 0; i < events.Count; i++)
                    events[i].Dispose();
                events.Clear();
                if (!batch.physics2D.events.isDisposed)
                    for (int i = 0; i < batch.physics2D.events.Count; i++)
                        events.Add(batch.physics2D.events[i].Duplicate());
            }
#endif
        }
    }
}
