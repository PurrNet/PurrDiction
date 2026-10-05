using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private sealed class LifecycleTick : IDisposable
        {
            public ulong tick;
            public PredictedHierarchyState topology;
            public bool ownsTopology;
            public BitPacker topologyDelta;
            public bool hasPreviousTopology;
            public readonly HashSet<PredictedComponentID> roster = new();
            public readonly Dictionary<PredictedComponentID, PredictedComponentID?> parents = new();
            public readonly List<LifecycleEntry> entrants = new();
            public BitPacker payload = BitPackerPool.Get();

            public void Dispose()
            {
                if (ownsTopology)
                    topology.Dispose();
                topologyDelta?.Dispose();
                payload?.Dispose();
                payload = null;
            }
        }

        private struct LifecycleEntry
        {
            public PredictedComponentID id;
            public PredictedObjectID root;
            public bool lifecycleChanged;
            public int origin;
            public int bits;
        }

        private LifecycleTick[] _lifecycleHistory;
        private LifecycleTick _lifecycleCaptureScratch;
        private ulong _latestLifecycleTick;
        private readonly Dictionary<PredictedObjectID, InstanceDetails> _previousLifecyclePieces = new();
        private readonly HashSet<PredictedObjectID> _changedLifecyclePieces = new();
        private readonly Stack<LifecycleTick> _lifecycleReplayPool = new();
        private readonly Stack<LifecycleReplay> _lifecycleReplays = new();
        private LifecycleTick _readingLifecycleBatch;
        private AddressedPredictionRecords.ReadRecord _readLifecycleEntrant;
        private readonly Dictionary<PredictedObjectID, int> _topologyCodecScratch = new();

        private void EnsureLifecycleHistoryCapacity()
        {
            int capacity = checked((int)verifiedHistoryWindowTicks + 1);
            if (_lifecycleHistory != null && _lifecycleHistory.Length == capacity)
                return;
            var previous = _lifecycleHistory;
            _lifecycleHistory = new LifecycleTick[capacity];
            if (previous == null)
                return;
            foreach (var entry in previous)
            {
                if (entry == null)
                    continue;
                if (entry.tick <= _latestLifecycleTick &&
                    _latestLifecycleTick - entry.tick <= verifiedHistoryWindowTicks)
                    _lifecycleHistory[(int)(entry.tick % (ulong)capacity)] = entry;
                else
                    entry.Dispose();
            }
        }

        private LifecycleTick GetLifecycleTick(ulong tick)
        {
            EnsureLifecycleHistoryCapacity();
            var entry = _lifecycleHistory[(int)(tick % (ulong)_lifecycleHistory.Length)];
            return entry != null && entry.tick == tick && tick <= _latestLifecycleTick &&
                   _latestLifecycleTick - tick <= verifiedHistoryWindowTicks ? entry : null;
        }

        // Capture while the owner exists; state writers cannot reconstruct an old tick from the live value.
        private void CaptureLifecycleHistory(ulong tick)
        {
            if (!hierarchy)
                return;
            if (GetLifecycleTick(tick) != null)
                return;
            if (_latestLifecycleTick != 0 && tick <= _latestLifecycleTick)
                throw new InvalidOperationException("Cannot reconstruct an uncaptured lifecycle tick.");

            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var peer = _clientFrames[i];
                PreparePlayerVisibility(peer.player, tick, peer.lastSentFrameTick);
            }

            hierarchy.GetLatestUnityState();
            hierarchy.RefreshVerifiedFromLive(tick);
            var previous = tick > 0 ? GetLifecycleTick(tick - 1) : null;
            _previousLifecyclePieces.Clear();
            if (previous != null)
                foreach (var record in previous.topology.spawnedPrefabs.list)
                    _previousLifecyclePieces[record.instanceId] = record;

            var captured = _lifecycleCaptureScratch ?? new LifecycleTick();
            _lifecycleCaptureScratch = null;
            if (captured.ownsTopology)
                captured.topology.Dispose();
            captured.topology = hierarchy.currentState.Duplicate();
            captured.ownsTopology = true;
            captured.tick = tick;
            captured.roster.Clear();
            captured.parents.Clear();
            captured.entrants.Clear();
            captured.payload.ResetPositionAndMode(false);
            try
            {
                captured.topologyDelta ??= BitPackerPool.Get();
                captured.topologyDelta.ResetPositionAndMode(false);
                captured.hasPreviousTopology = previous != null;
                if (previous != null)
                    HistoricalTopologyCodec.Write(previous.topology, captured.topology,
                        captured.topologyDelta, _topologyCodecScratch);
                var changed = _changedLifecyclePieces;
                changed.Clear();
                foreach (var record in captured.topology.spawnedPrefabs.list)
                {
                    if (!_previousLifecyclePieces.TryGetValue(record.instanceId, out var old) ||
                        old.prefabId != record.prefabId || old.pieceIndex.value != record.pieceIndex.value ||
                        old.owner != record.owner || !Nullable.Equals(old.parent, record.parent))
                        changed.Add(record.instanceId);
                }

                for (int i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system == hierarchy || system.isEventHandler)
                        continue;
                    captured.roster.Add(system.id);
                    bool lifecycleChanged = previous == null || !previous.roster.Contains(system.id) ||
                                changed.Contains(system.id.objectId);
                    // InstanceDetails stores authored attachments; PredictedParent stores runtime parenting.
                    if (system is PredictedParent parent)
                    {
                        parent.GetLatestUnityState();
                        var link = parent.currentState.parent;
                        captured.parents.Add(system.id, link);
                        lifecycleChanged |= previous == null || !previous.parents.TryGetValue(system.id, out var oldLink) ||
                                !Nullable.Equals(oldLink, link);
                    }
                    bool entered = false;
                    if (!lifecycleChanged)
                    {
                        foreach (var pair in _playerVisibility)
                        {
                            var timeline = pair.Value;
                            if (!timeline.isPassThrough && timeline.WasVisibleAt(system.rootObjectId, tick) &&
                                (tick == 0 || !timeline.WasVisibleAt(system.rootObjectId, tick - 1)))
                            {
                                entered = true;
                                break;
                            }
                        }
                    }
                    if (!lifecycleChanged && !entered)
                        continue;
                    int origin = captured.payload.positionInBits;
                    system.RunWriteFirstState(tick, captured.payload);
                    captured.entrants.Add(new LifecycleEntry
                    {
                        id = system.id, root = system.rootObjectId, lifecycleChanged = lifecycleChanged, origin = origin,
                        bits = captured.payload.positionInBits - origin
                    });
                }
            }
            catch
            {
                captured.Dispose();
                throw;
            }

            int index = (int)(tick % (ulong)_lifecycleHistory.Length);
            _lifecycleCaptureScratch = _lifecycleHistory[index];
            _lifecycleHistory[index] = captured;
            _latestLifecycleTick = tick;
            // Tick jumps can leave expired slots beyond the one just overwritten.
            for (int i = 0; i < _lifecycleHistory.Length; i++)
            {
                var old = _lifecycleHistory[i];
                if (old != null && tick - old.tick > verifiedHistoryWindowTicks)
                {
                    old.Dispose();
                    _lifecycleHistory[i] = null;
                }
            }
        }

        private bool HasReplayHistory(ulong baseline, ulong through)
        {
            if (baseline > through || through - baseline > verifiedHistoryWindowTicks)
                return false;
            if (hierarchy && GetLifecycleTick(baseline) == null)
                return false;
            for (ulong tick = baseline + 1; tick <= through; tick++)
            {
                if (!TryGetInputBlockForTick(tick, out _) || hierarchy && GetLifecycleTick(tick) == null)
                    return false;
            }
            return true;
        }

        private void DisposeLifecycleHistory()
        {
            if (_lifecycleHistory != null)
                foreach (var entry in _lifecycleHistory)
                    entry?.Dispose();
            _lifecycleHistory = null;
            _lifecycleCaptureScratch?.Dispose();
            _lifecycleCaptureScratch = null;
            _latestLifecycleTick = 0;
            _previousLifecyclePieces.Clear();
            _changedLifecyclePieces.Clear();
            _topologyCodecScratch.Clear();
            while (_lifecycleReplayPool.Count > 0)
                _lifecycleReplayPool.Pop().Dispose();
            _lifecycleReplays.Clear();
        }

        private bool IsLifecycleEntrant(ulong tick, PredictedComponentID id)
        {
            var entry = GetLifecycleTick(tick);
            if (entry == null)
                return false;
            for (int i = 0; i < entry.entrants.Count; i++)
            {
                if (entry.entrants[i].id.Equals(id) && entry.entrants[i].lifecycleChanged)
                    return true;
            }
            return false;
        }

        private void WriteLifecycleHistory(BitPacker frame, ulong baselineTick, PlayerVisibilityTimeline timeline,
            ulong entrantsFromTick)
        {
            Packer<bool>.Write(frame, hierarchy);
            if (!hierarchy)
                return;
            uint count = localTick > baselineTick ? checked((uint)(localTick - baselineTick - 1)) : 0;
            Packer<PackedUInt>.Write(frame, count);
            using var topologyPatch = BitPackerPool.Get();
            using var records = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();
            var baseline = GetLifecycleTick(baselineTick) ?? throw new MissingPredictionBaselineException(
                "Missing acknowledged authoritative lifecycle history.");
            // Captured topology is borrowed; only the visibility projections are owned here.
            var previousTopology = baseline.topology;
            PredictedHierarchyState previousProjection = default;
            try
            {
                if (!timeline.isPassThrough)
                    previousProjection = PredictedHierarchy.BuildVisibilityProjection(baseline.topology, timeline, baselineTick);
                for (ulong tick = baselineTick + 1; tick < localTick; tick++)
                {
                    var entry = GetLifecycleTick(tick) ?? throw new MissingPredictionBaselineException(
                        $"Missing authoritative lifecycle history at tick {tick}.");
                    BitPacker patch = entry.topologyDelta;
                    PredictedHierarchyState projection = default;
                    try
                    {
                        if (!timeline.isPassThrough)
                            projection = PredictedHierarchy.BuildVisibilityProjection(entry.topology, timeline, tick);
                        else if (!entry.hasPreviousTopology)
                            throw new MissingPredictionBaselineException($"Missing adjacent lifecycle baseline at tick {tick}.");
                        bool repeat = timeline.isPassThrough
                            ? Packer.AreEqualRef(ref previousTopology, ref entry.topology)
                            : Packer.AreEqualRef(ref previousProjection, ref projection);
                        Packer<bool>.Write(frame, repeat);
                        if (!repeat)
                        {
                            if (!timeline.isPassThrough)
                            {
                                topologyPatch.ResetPositionAndMode(false);
                                HistoricalTopologyCodec.Write(previousProjection, projection, topologyPatch, _topologyCodecScratch);
                                patch = topologyPatch;
                            }
                            Packer<PackedUInt>.Write(frame, (uint)patch.positionInBits);
                            frame.WriteBitsWithoutConsumingIt(patch, patch.positionInBits);
                        }
                        if (timeline.isPassThrough)
                            previousTopology = entry.topology;
                        else
                        {
                            previousProjection.Dispose();
                            previousProjection = projection;
                            projection = default;
                        }
                    }
                    finally { projection.Dispose(); }

                    if (tick < entrantsFromTick)
                    {
                        lifecycleEntrantsOmittedTotal += (ulong)entry.entrants.Count;
                        AddressedPredictionRecords.WriteSectionCount(0, frame);
                        continue;
                    }
                    records.ResetPositionAndMode(false);
                    int entries = 0;
                    foreach (var state in entry.entrants)
                    {
                        bool visible = timeline.isPassThrough || state.root.instanceId.value == 1 ||
                                       timeline.WasVisibleAt(state.root, tick);
                        bool needed = state.lifecycleChanged || !timeline.isPassThrough &&
                                      !timeline.WasVisibleAt(state.root, tick - 1);
                        if (!visible || !needed)
                            continue;
                        payload.ResetPositionAndMode(false);
                        payload.WriteBitDataWithoutConsumingIt(new BitData(entry.payload, state.origin, state.bits));
                        AddressedPredictionRecords.WriteRecord(records, state.id, true, payload);
                        entries++;
                    }
                    AddressedPredictionRecords.WriteSectionCount(entries, frame);
                    frame.WriteBitsWithoutConsumingIt(records, records.positionInBits);
                }
            }
            finally { previousProjection.Dispose(); }
        }

        private sealed class LifecycleReplay : IDisposable
        {
            public PredictionManager owner;
            public ulong firstTick;
            public readonly List<LifecycleTick> ticks = new();
            public void Dispose()
            {
                if (owner == null)
                    return;

                foreach (var tick in ticks)
                {
                    if (tick.ownsTopology)
                        tick.topology.Dispose();
                    tick.topology = default;
                    tick.ownsTopology = false;
                    tick.roster.Clear();
                    tick.parents.Clear();
                    tick.entrants.Clear();
                    tick.payload.ResetPositionAndMode(false);
                    owner._lifecycleReplayPool.Push(tick);
                }
                ticks.Clear();

                var pool = owner._lifecycleReplays;
                owner = null;
                pool.Push(this);
            }
        }

        private LifecycleReplay ReadLifecycleHistory(BitPacker frame, ulong baselineTick, ulong serverTick, int endBit)
        {
            var result = _lifecycleReplays.Count > 0 ? _lifecycleReplays.Pop() : new LifecycleReplay();
            result.owner = this;
            result.firstTick = baselineTick + 1;
            try
            {
                if (frame.positionInBits >= endBit)
                    throw new MissingPredictionBaselineException("Missing lifecycle section.");
                bool present = Packer<bool>.Read(frame);
                if (present != (bool)hierarchy)
                    throw new MissingPredictionBaselineException("Authoritative lifecycle hierarchy mismatch.");
                if (!present)
                    return result;
                uint count = Packer<PackedUInt>.Read(frame);
                if (serverTick <= baselineTick || count != serverTick - baselineTick - 1 ||
                    count > verifiedHistoryWindowTicks || frame.positionInBits > endBit)
                    throw new MissingPredictionBaselineException("Invalid authoritative lifecycle interval.");
                // Equal snapshots coalesce, so a valid baseline may use a carried-forward entry.
                if (_frameApplyHadBaselineFailure ||
                    !hierarchy.TryGetVerifiedState(baselineTick, out _, out var previousTopology))
                    throw new MissingPredictionBaselineException("Missing acknowledged lifecycle topology.");
                for (uint i = 0; i < count; i++)
                {
                    var batch = _lifecycleReplayPool.Count > 0 ? _lifecycleReplayPool.Pop() : new LifecycleTick();
                    batch.tick = baselineTick + 1 + i;
                    result.ticks.Add(batch);
                    bool repeat = Packer<bool>.Read(frame);
                    if (repeat)
                    {
                        batch.topology = previousTopology;
                    }
                    else
                    {
                        uint bits = Packer<PackedUInt>.Read(frame);
                        if (frame.positionInBits > endBit || bits == 0 || bits > endBit - frame.positionInBits)
                            throw new MissingPredictionBaselineException("Truncated lifecycle topology.");
                        using var payload = BitPackerPool.Get();
                        payload.WriteBits(frame, (int)bits);
                        payload.ResetPositionAndMode(true);
                        batch.ownsTopology = true;
                        batch.topology = HistoricalTopologyCodec.Read(previousTopology, payload, (int)bits);
                        if (payload.positionInBits != bits)
                            throw new MissingPredictionBaselineException("Incomplete lifecycle topology.");
                    }
                    previousTopology = batch.topology;
                    int start = frame.positionInBits;
                    AddressedPredictionRecords.SkipSection(frame, endBit);
                    int after = frame.positionInBits;
                    frame.SetBitPosition(start);
                    var outerBatch = _readingLifecycleBatch;
                    _readingLifecycleBatch = batch;
                    try
                    {
                        AddressedPredictionRecords.ReadSection(_readLifecycleEntrant ??= ReadLifecycleEntrant, frame);
                    }
                    finally
                    {
                        _readingLifecycleBatch = outerBatch;
                    }
                    frame.SetBitPosition(after);
                }
                return result;
            }
            catch (Exception error)
            {
                result.Dispose();
                if (error is MissingPredictionBaselineException)
                    throw;
                throw new MissingPredictionBaselineException($"Invalid lifecycle transcript: {error.Message}");
            }
        }

        private void ReadLifecycleEntrant(PredictedComponentID id, bool full, BitPacker payload, int bits)
        {
            var batch = _readingLifecycleBatch;
            if (!full || !batch.roster.Add(id) || bits == 0)
                throw new MissingPredictionBaselineException("Invalid lifecycle entrant state.");
            int origin = batch.payload.positionInBits;
            batch.payload.WriteBitDataWithoutConsumingIt(new BitData(payload, 0, bits));
            batch.entrants.Add(new LifecycleEntry { id = id, origin = origin, bits = bits });
        }

        private void ApplyLifecycleHistory(LifecycleReplay history, ulong tick)
        {
            try { ApplyLifecycleTick(history, tick); }
            catch (Exception error)
            {
                if (error is MissingPredictionBaselineException)
                    throw;
                throw new MissingPredictionBaselineException($"Cannot apply lifecycle tick {tick}: {error.Message}");
            }
        }

        private void ApplyLifecycleTick(LifecycleReplay history, ulong tick)
        {
            localTickInContext = tick;
            if (!hierarchy)
                return;
            if (tick < history.firstTick || tick - history.firstTick >= (ulong)history.ticks.Count)
                throw new MissingPredictionBaselineException($"Missing lifecycle tick {tick}.");
            var batch = history.ticks[(int)(tick - history.firstTick)];
            ApplyPendingRemoteVisibilityDeletes(tick);
            hierarchy.ApplyHistoricalTopology(batch.topology);
            using var payload = BitPackerPool.Get();
            foreach (var entry in batch.entrants)
            {
                if (!_instanceMap.TryGetValue(entry.id, out var system) || !system || system == hierarchy)
                    throw new MissingPredictionBaselineException($"Cannot resolve lifecycle entrant {entry.id} at tick {tick}.");
                payload.ResetPositionAndMode(false);
                payload.WriteBitDataWithoutConsumingIt(new BitData(batch.payload, entry.origin, entry.bits));
                payload.ResetPositionAndMode(true);
                ApplyAddressedState(system, payload, true, tick, tick, tick, false);
                if (payload.positionInBits != entry.bits)
                    throw new MissingPredictionBaselineException($"Incomplete lifecycle entrant {entry.id} at tick {tick}.");
            }
            for (int i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system != hierarchy && !system.isEventHandler &&
                    hierarchy.WasMaterializedByVerifiedApply(system.id.objectId) && !batch.roster.Contains(system.id))
                    throw new MissingPredictionBaselineException($"Missing entering state for recreated {system.id} at tick {tick}.");
            }
            SyncTransforms();
        }
    }
}
