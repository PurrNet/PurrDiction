using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private struct VerifiedInputTick
        {
            public int firstEntry;
            public int count;
        }

        private readonly List<VerifiedInputTick> _verifiedInputTicks = new();
        private readonly List<InputHistorySpan> _verifiedInputEntries = new();
        private readonly HashSet<PredictedComponentID> _verifiedInputIds = new();
        private readonly List<bool> _verifiedInputRepeatScratch = new();
        private readonly List<bool> _verifiedInputDeltaScratch = new();
        private readonly List<bool> _verifiedInputRestoredScratch = new();
        private readonly List<VerifiedInputTick> _verifiedViewOffsetTicks = new();
        private readonly List<PlayerViewOffset> _verifiedViewOffsets = new();
        private BitPacker _verifiedInputPayload;
        private ulong _verifiedInputFrom;
        private ulong _verifiedInputThrough;

        private void ClearVerifiedInputTranscript()
        {
            _verifiedInputPayload?.Dispose();
            _verifiedInputPayload = null;
            _verifiedInputTicks.Clear();
            _verifiedInputEntries.Clear();
            _verifiedInputIds.Clear();
            _verifiedInputRepeatScratch.Clear();
            _verifiedInputDeltaScratch.Clear();
            _verifiedInputRestoredScratch.Clear();
            _verifiedViewOffsetTicks.Clear();
            _verifiedViewOffsets.Clear();
        }

        private void ReadTranscriptViewOffsets(BitPacker frame, int frameEndBit, ulong tick, bool allowRepeat)
        {
            uint count = Packer<PackedUInt>.Read(frame);
            if (frame.positionInBits > frameEndBit || count > (uint)(frameEndBit - frame.positionInBits))
                throw new MissingPredictionBaselineException($"Invalid view offset count at tick {tick}.");

            int first = _verifiedViewOffsets.Count;
            int tickIndex = _verifiedViewOffsetTicks.Count;
            _verifiedViewOffsetTicks.Add(new VerifiedInputTick { firstEntry = first, count = (int)count });
            if (count == 0)
                return;

            bool sameRoster = allowRepeat && Packer<bool>.Read(frame);
            if (sameRoster)
            {
                var previous = _verifiedViewOffsetTicks[tickIndex - 1];
                if (previous.count != (int)count)
                    throw new MissingPredictionBaselineException(
                        $"View offset roster at tick {tick} does not match the previous tick it repeats.");
                for (var i = 0; i < count; i++)
                    _verifiedViewOffsets.Add(new PlayerViewOffset(_verifiedViewOffsets[previous.firstEntry + i].player, 0));
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var player = Packer<PlayerID>.Read(frame);
                    if (frame.positionInBits > frameEndBit)
                        throw new MissingPredictionBaselineException($"Truncated view offset roster at tick {tick}.");
                    _verifiedViewOffsets.Add(new PlayerViewOffset(player, 0));
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (frame.positionInBits + ViewOffsetBits > frameEndBit)
                    throw new MissingPredictionBaselineException($"Truncated view offsets at tick {tick}.");
                uint quantized = (uint)frame.ReadBits((byte)ViewOffsetBits);
                _verifiedViewOffsets[first + i] = new PlayerViewOffset(_verifiedViewOffsets[first + i].player, quantized);
            }
        }

        private void BeginVerifiedInputTranscript(ulong firstTick, ulong lastTick)
        {
            ClearVerifiedInputTranscript();
            _verifiedInputPayload = BitPackerPool.Get();
            _verifiedInputFrom = firstTick;
            _verifiedInputThrough = lastTick;
        }

        private void BeginVerifiedInputTick()
        {
            _verifiedInputIds.Clear();
            _verifiedInputTicks.Add(new VerifiedInputTick { firstEntry = _verifiedInputEntries.Count });
        }

        private void StageVerifiedInput(
            ulong tick, PredictedComponentID id, BitPacker source, int origin, int length)
        {
            int destinationOrigin = _verifiedInputPayload.positionInBits;
            _verifiedInputPayload.WriteBitDataWithoutConsumingIt(new BitData(source, origin, length));
            StageVerifiedInputSpan(tick, id, destinationOrigin, length);
        }

        private void StageRepeatedVerifiedInput(ulong tick, in InputHistorySpan previous)
            => StageVerifiedInputSpan(tick, previous.id, previous.bitOrigin, previous.bitLength);

        // The server used exactly the bits this peer uploaded for the tick, so they were not sent back.
        private void StageRestoredVerifiedInput(ulong tick, PredictedComponentID id)
        {
            if (!TryGetUploadedInput(tick, id, out var uploaded))
                throw new MissingPredictionBaselineException(
                    $"Authoritative input for {id} at tick {tick} refers to an upload this peer no longer holds.");
            int origin = _verifiedInputPayload.positionInBits;
            _verifiedInputPayload.WriteBitDataWithoutConsumingIt(uploaded);
            StageVerifiedInputSpan(tick, id, origin, (int)uploaded.bitLength.value);
        }

        private struct UploadedInputTick
        {
            public BitPacker bits;
            public List<InputHistorySpan> spans;
        }

        private readonly Dictionary<ulong, UploadedInputTick> _uploadedInputs = new();
        private readonly List<ulong> _uploadedInputPruneScratch = new();

        internal void RecordUploadedInputs(ulong tick, BitPacker block, List<InputHistorySpan> spans)
        {
            // Only the first upload of a tick can be the one the server consumed.
            if (_uploadedInputs.ContainsKey(tick))
                return;
            var entry = new UploadedInputTick { bits = BitPackerPool.Get(), spans = new List<InputHistorySpan>(spans) };
            entry.bits.ResetPositionAndMode(false);
            entry.bits.WriteBitDataWithoutConsumingIt(new BitData(block, 0, block.positionInBits));
            _uploadedInputs[tick] = entry;

            ulong window = verifiedHistoryWindowTicks;

            if (_verifiedServerTick <= window)
                return;

            _uploadedInputPruneScratch.Clear();
            foreach (var recorded in _uploadedInputs.Keys)
            {
                if (recorded <= _verifiedServerTick - window)
                    _uploadedInputPruneScratch.Add(recorded);
            }
            for (int i = 0; i < _uploadedInputPruneScratch.Count; i++)
            {
                _uploadedInputs[_uploadedInputPruneScratch[i]].bits.Dispose();
                _uploadedInputs.Remove(_uploadedInputPruneScratch[i]);
            }
        }

        private bool TryGetUploadedInput(ulong tick, PredictedComponentID id, out BitData bits)
        {
            bits = default;
            if (!_uploadedInputs.TryGetValue(tick, out var entry))
                return false;
            for (int i = 0; i < entry.spans.Count; i++)
            {
                var span = entry.spans[i];
                if (!span.id.Equals(id))
                    continue;
                bits = new BitData(entry.bits, span.bitOrigin, span.bitLength);
                return true;
            }
            return false;
        }

        private void ClearUploadedInputs()
        {
            foreach (var entry in _uploadedInputs.Values)
                entry.bits.Dispose();
            _uploadedInputs.Clear();
        }

        private void StageVerifiedInputSpan(ulong tick, PredictedComponentID id, int origin, int length)
        {
            if (!_verifiedInputIds.Add(id))
                throw new MissingPredictionBaselineException($"Duplicate authoritative input for {id} at tick {tick}.");
            _verifiedInputEntries.Add(new InputHistorySpan
            {
                id = id,
                bitOrigin = origin,
                bitLength = length
            });
            int index = _verifiedInputTicks.Count - 1;
            var batch = _verifiedInputTicks[index];
            batch.count++;
            _verifiedInputTicks[index] = batch;
        }

        private void ReadInputHistory(BitPacker frame, ulong serverTick, ulong baselineTick, int frameEndBit)
        {
            using var marker = ReadInputHistoryMarker.Auto();
            if (baselineTick > serverTick || serverTick - baselineTick > verifiedHistoryWindowTicks ||
                frame.positionInBits >= frameEndBit)
                throw new MissingPredictionBaselineException("Invalid authoritative input history window.");

            uint count = Packer<PackedUInt>.Read(frame);
            if (frame.positionInBits > frameEndBit || count != serverTick - baselineTick)
                throw new MissingPredictionBaselineException(
                    $"Authoritative inputs must cover every tick after {baselineTick} through {serverTick}.");

            BeginVerifiedInputTranscript(baselineTick + 1, serverTick);
            for (uint k = 0; k < count; k++)
            {
                ulong tick = baselineTick + 1 + k;
                if (frame.positionInBits >= frameEndBit)
                    throw new MissingPredictionBaselineException($"Missing authoritative input block at tick {tick}.");
                uint entries = Packer<PackedUInt>.Read(frame);
                if (frame.positionInBits > frameEndBit || entries > (uint)(frameEndBit - frame.positionInBits))
                    throw new MissingPredictionBaselineException($"Invalid authoritative input count at tick {tick}.");
                BeginVerifiedInputTick();
                if (entries == 0)
                {
                    _verifiedViewOffsetTicks.Add(new VerifiedInputTick { firstEntry = _verifiedViewOffsets.Count });
                    continue;
                }

                var previousBatch = k > 0 ? _verifiedInputTicks[(int)k - 1] : default;
                bool sameRoster = k > 0 && Packer<bool>.Read(frame);
                ReadTranscriptViewOffsets(frame, frameEndBit, tick, k > 0 && previousBatch.count > 0);
                _verifiedInputRepeatScratch.Clear();
                _verifiedInputDeltaScratch.Clear();
                _verifiedInputRestoredScratch.Clear();
                if (sameRoster)
                {
                    if ((uint)previousBatch.count != entries || entries > (uint)(frameEndBit - frame.positionInBits))
                        throw new MissingPredictionBaselineException(
                            $"Authoritative input roster at tick {tick} does not match the previous tick it repeats.");
                    for (uint e = 0; e < entries; e++)
                    {
                        bool repeats = ReadTranscriptFlag(frame, frameEndBit, tick);
                        bool delta = !repeats && ReadTranscriptFlag(frame, frameEndBit, tick);
                        bool restored = !repeats && !delta && ReadTranscriptFlag(frame, frameEndBit, tick);
                        _verifiedInputRepeatScratch.Add(repeats);
                        _verifiedInputDeltaScratch.Add(delta);
                        _verifiedInputRestoredScratch.Add(restored);
                    }
                }
                SkipTranscriptPadding(frame, frameEndBit, tick);

                for (int e = 0; e < (int)entries; e++)
                {
                    if (sameRoster && _verifiedInputRepeatScratch[e])
                    {
                        StageRepeatedVerifiedInput(tick, _verifiedInputEntries[previousBatch.firstEntry + e]);
                        continue;
                    }
                    var previous = sameRoster ? _verifiedInputEntries[previousBatch.firstEntry + e] : default;
                    var id = sameRoster ? previous.id : Packer<PredictedComponentID>.Read(frame);
                    if (frame.positionInBits > frameEndBit)
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    bool restored = sameRoster ? _verifiedInputRestoredScratch[e] : ReadTranscriptFlag(frame, frameEndBit, tick);
                    if (restored)
                    {
                        StageRestoredVerifiedInput(tick, id);
                        continue;
                    }
                    if (sameRoster && _verifiedInputDeltaScratch[e])
                    {
                        int destinationOrigin = _verifiedInputPayload.positionInBits;
                        var previousBits = new BitData(_verifiedInputPayload, previous.bitOrigin, previous.bitLength);
                        InputHistoryDelta.Read(frame, frameEndBit, in previousBits, _verifiedInputPayload);
                        StageVerifiedInputSpan(tick, id, destinationOrigin, previous.bitLength);
                        continue;
                    }
                    uint bits = Packer<PackedUInt>.Read(frame);
                    int origin = frame.positionInBits;
                    if (origin > frameEndBit || bits == 0 || bits > (uint)(frameEndBit - origin))
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    frame.SkipBits(checked((int)bits));
                    StageVerifiedInput(tick, id, frame, origin, (int)bits);
                }
            }
        }

        private static bool ReadTranscriptFlag(BitPacker frame, int frameEndBit, ulong tick)
        {
            if (frame.positionInBits >= frameEndBit)
                throw new MissingPredictionBaselineException($"Truncated input mode at tick {tick}.");
            return Packer<bool>.Read(frame);
        }

        // Decode after rollback has recreated identities; predicted history does not prove receipt.
        private void ApplyVerifiedInputs(ulong tick)
        {
            if (_verifiedInputPayload == null || tick < _verifiedInputFrom || tick > _verifiedInputThrough ||
                tick - _verifiedInputFrom >= (ulong)_verifiedInputTicks.Count)
                throw new MissingPredictionBaselineException($"Missing authoritative input transcript at tick {tick}.");

            int tickIndex = (int)(tick - _verifiedInputFrom);
            if (tickIndex < _verifiedViewOffsetTicks.Count)
            {
                var offsets = _verifiedViewOffsetTicks[tickIndex];
                for (int i = offsets.firstEntry; i < offsets.firstEntry + offsets.count; i++)
                    RecordViewOffset(_verifiedViewOffsets[i].player, tick, _verifiedViewOffsets[i].quantized);
            }

            var batch = _verifiedInputTicks[tickIndex];
            _verifiedInputIds.Clear();
            using var payload = BitPackerPool.Get();
            for (int i = batch.firstEntry; i < batch.firstEntry + batch.count; i++)
            {
                var entry = _verifiedInputEntries[i];
                if (!_instanceMap.TryGetValue(entry.id, out var system) || !system || !system.hasInput)
                    throw new MissingPredictionBaselineException(
                        $"Cannot resolve authoritative input identity {entry.id} at tick {tick}.");
                payload.ResetPositionAndMode(false);
                payload.WriteBitDataWithoutConsumingIt(
                    new BitData(_verifiedInputPayload, entry.bitOrigin, entry.bitLength));
                payload.ResetPositionAndMode(true);
                try
                {
                    system.ReadFirstInput(tick, payload);
                }
                catch (Exception error)
                {
                    throw new MissingPredictionBaselineException(
                        $"Cannot read authoritative input for {entry.id} at tick {tick}: {error.Message}");
                }
                if (payload.positionInBits != entry.bitLength || !system.HasInputAt(tick))
                    throw new MissingPredictionBaselineException(
                        $"Incomplete authoritative input for {entry.id} at tick {tick}.");
                _verifiedInputIds.Add(entry.id);
            }

            for (int i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.hasInput && !system.SkipsCurrentSimulationPhase() && !_verifiedInputIds.Contains(system.id))
                    throw new MissingPredictionBaselineException(
                        $"Missing authoritative input for {system.id} at tick {tick}.");
            }
        }
    }
}
