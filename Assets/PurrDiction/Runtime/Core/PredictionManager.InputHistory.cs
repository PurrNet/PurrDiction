using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        internal readonly struct CachedInputEntry
        {
            public readonly PredictedComponentID id;
            public readonly PredictedObjectID rootId;
            public readonly int bitOrigin;
            public readonly int bitLength;
            // The previous tick contains identical bits for this ID under the same root.
            public readonly bool repeatsPrevious;
            public readonly int deltaOrigin;
            public readonly int deltaLength;
            // The owner uploaded exactly these bits for this tick, so it can restore them itself.
            public readonly PlayerID? owner;
            public readonly bool asUploaded;

            public CachedInputEntry(
                PredictedComponentID id,
                PredictedObjectID rootId,
                int bitOrigin,
                int bitLength,
                bool repeatsPrevious,
                int deltaOrigin,
                int deltaLength,
                PlayerID? owner,
                bool asUploaded)
            {
                this.id = id;
                this.rootId = rootId;
                this.bitOrigin = bitOrigin;
                this.bitLength = bitLength;
                this.repeatsPrevious = repeatsPrevious;
                this.deltaOrigin = deltaOrigin;
                this.deltaLength = deltaLength;
                this.owner = owner;
                this.asUploaded = asUploaded;
            }

            public bool RestoredBy(PlayerID player) => asUploaded && owner.HasValue && owner.Value == player;
        }

        private struct CachedInputBlock
        {
            public ulong tick;
            public bool captured;
            public BitPacker packer;
            public BitPacker deltas;
            public List<CachedInputEntry> entries;
            public bool rosterRepeatsPrevious;
            public List<PlayerViewOffset> viewOffsets;
            public bool viewOffsetRosterRepeatsPrevious;
        }

        private CachedInputBlock[] _inputBlockCache;
        private CachedInputBlock _inputBlockScratch;
        private readonly Dictionary<PredictedComponentID, int> _previousInputEntryIndex = new();
        private ulong _latestCapturedInputTick;
        private bool _hasCapturedInputHistory;

        // Capture after PrepareInput, even without observers; later roster changes cannot reconstruct this tick.
        private void CaptureInputHistory(ulong tick)
        {
            EnsureInputHistoryCapacity();
            if (_hasCapturedInputHistory && tick <= _latestCapturedInputTick)
            {
                if (TryGetInputBlockForTick(tick, out _))
                    return;
                throw new InvalidOperationException(
                    $"Cannot capture authoritative input tick {tick} after tick {_latestCapturedInputTick}.");
            }

            ref var scratch = ref _inputBlockScratch;
            scratch.packer ??= BitPackerPool.Get();
            scratch.deltas ??= BitPackerPool.Get();
            scratch.entries ??= new List<CachedInputEntry>();
            scratch.captured = false;
            scratch.packer.ResetPositionAndMode(false);
            scratch.deltas.ResetPositionAndMode(false);
            scratch.entries.Clear();
            scratch.viewOffsets ??= new List<PlayerViewOffset>();
            CollectViewOffsets(tick, scratch.viewOffsets);

            _previousInputEntryIndex.Clear();
            CachedInputBlock previous = default;
            bool hasPrevious = tick > 0 && TryGetInputBlockForTick(tick - 1, out previous);
            if (hasPrevious)
            {
                for (var i = 0; i < previous.entries.Count; i++)
                    _previousInputEntryIndex[previous.entries[i].id] = i;
            }

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.hasInput)
                    continue;
                if (!system.HasInputAt(tick))
                    throw new MissingPredictionBaselineException(
                        $"Authoritative input {system.id} was not prepared at tick {tick}.");

                var id = system.id;
                var rootId = system.rootObjectId;
                int origin = scratch.packer.positionInBits;
                system.WriteFirstInput(tick, scratch.packer);
                int length = scratch.packer.positionInBits - origin;

                bool repeats = false;
                int deltaOrigin = scratch.deltas.positionInBits;
                int deltaLength = 0;
                if (hasPrevious && _previousInputEntryIndex.TryGetValue(id, out int previousIndex))
                {
                    var previousEntry = previous.entries[previousIndex];
                    if (previousEntry.rootId.Equals(rootId))
                    {
                        var currentBits = new BitData(scratch.packer, origin, length);
                        var previousBits = new BitData(previous.packer, previousEntry.bitOrigin, previousEntry.bitLength);
                        repeats = currentBits.Equals(previousBits);
                        if (!repeats && InputHistoryDelta.TryWrite(scratch.deltas, in previousBits, in currentBits))
                            deltaLength = scratch.deltas.positionInBits - deltaOrigin;
                    }
                }

                bool asUploaded = system.TryGetUploadedInputBits(tick, out var uploaded) &&
                                  new BitData(scratch.packer, origin, length).Equals(uploaded);
                scratch.entries.Add(new CachedInputEntry(id, rootId, origin, length, repeats, deltaOrigin, deltaLength,
                    system.owner, asUploaded));
            }

            bool sameRoster = hasPrevious && previous.entries.Count == scratch.entries.Count;
            for (var i = 0; sameRoster && i < scratch.entries.Count; i++)
                sameRoster = scratch.entries[i].id.Equals(previous.entries[i].id);
            scratch.rosterRepeatsPrevious = sameRoster;

            bool sameOffsetRoster = hasPrevious && previous.viewOffsets != null &&
                                    previous.viewOffsets.Count == scratch.viewOffsets.Count;
            for (var i = 0; sameOffsetRoster && i < scratch.viewOffsets.Count; i++)
                sameOffsetRoster = scratch.viewOffsets[i].player == previous.viewOffsets[i].player;
            scratch.viewOffsetRosterRepeatsPrevious = sameOffsetRoster;

            // Publish only after user packers succeed, preserving retained history if one throws.
            int index = (int)(tick % (ulong)_inputBlockCache.Length);
            var replaced = _inputBlockCache[index];
            scratch.tick = tick;
            scratch.captured = true;
            _inputBlockCache[index] = scratch;
            _inputBlockScratch = replaced;
            _inputBlockScratch.captured = false;

            // Tick jumps can leave expired ring slots that were not overwritten.
            if (_hasCapturedInputHistory && tick - _latestCapturedInputTick > 1)
                PruneInputHistory(tick);
            _latestCapturedInputTick = tick;
            _hasCapturedInputHistory = true;
        }

        private static void WriteTranscriptViewOffsets(BitPacker frame, in CachedInputBlock block, bool allowRepeat)
        {
            var offsets = block.viewOffsets;
            int count = offsets?.Count ?? 0;
            Packer<PackedUInt>.Write(frame, (uint)count);
            if (count == 0)
                return;

            bool sameRoster = allowRepeat && block.viewOffsetRosterRepeatsPrevious;
            if (allowRepeat)
                Packer<bool>.Write(frame, sameRoster);

            if (!sameRoster)
            {
                for (var i = 0; i < count; i++)
                    Packer<PlayerID>.Write(frame, offsets[i].player);
            }

            for (var i = 0; i < count; i++)
                frame.WriteBits(offsets[i].quantized, (byte)ViewOffsetBits);
        }

        private static void WriteTranscriptEntry(BitPacker frame, in CachedInputBlock block, int index, bool sameRoster,
            PlayerID player)
        {
            var entry = block.entries[index];
            if (!sameRoster)
            {
                Packer<PredictedComponentID>.Write(frame, entry.id);
                bool restored = entry.RestoredBy(player);
                Packer<bool>.Write(frame, restored);
                if (restored)
                    return;
            }
            else if (entry.deltaLength > 0)
            {
                frame.WriteBitDataWithoutConsumingIt(new BitData(block.deltas, entry.deltaOrigin, entry.deltaLength));
                return;
            }
            Packer<PackedUInt>.Write(frame, (uint)entry.bitLength);
            frame.WriteBitDataWithoutConsumingIt(new BitData(block.packer, entry.bitOrigin, entry.bitLength));
        }

        private static void WriteTranscriptPadding(BitPacker frame)
        {
            int pad = (8 - frame.positionInBits % 8) % 8;
            if (pad > 0)
                frame.WriteBits(0UL, (byte)pad);
        }

        private static void SkipTranscriptPadding(BitPacker frame, int frameEndBit, ulong tick)
        {
            int pad = (8 - frame.positionInBits % 8) % 8;
            if (pad == 0)
                return;
            if (frame.positionInBits + pad > frameEndBit)
                throw new MissingPredictionBaselineException($"Truncated authoritative input block at tick {tick}.");
            frame.SkipBits(pad);
        }

        private void EnsureInputHistoryCapacity()
        {
            int capacity = checked((int)verifiedHistoryWindowTicks + 1);
            if (_inputBlockCache != null && _inputBlockCache.Length == capacity)
                return;

            var previous = _inputBlockCache;
            _inputBlockCache = new CachedInputBlock[capacity];
            if (previous == null)
                return;

            for (var i = 0; i < previous.Length; i++)
            {
                ref var block = ref previous[i];
                if (block.captured && _hasCapturedInputHistory &&
                    block.tick <= _latestCapturedInputTick &&
                    _latestCapturedInputTick - block.tick <= verifiedHistoryWindowTicks)
                {
                    _inputBlockCache[(int)(block.tick % (ulong)capacity)] = block;
                    block = default;
                }
                else
                {
                    DisposeInputBlock(ref block);
                }
            }

            // Release the spare buffer capacity retained from the previous tick rate.
            DisposeInputBlock(ref _inputBlockScratch);
        }

        private void PruneInputHistory(ulong throughTick)
        {
            for (var i = 0; i < _inputBlockCache.Length; i++)
            {
                ref var block = ref _inputBlockCache[i];
                if (block.captured &&
                    (block.tick > throughTick || throughTick - block.tick > verifiedHistoryWindowTicks))
                    DisposeInputBlock(ref block);
            }
        }

        private bool TryGetInputBlockForTick(ulong tick, out CachedInputBlock block)
        {
            EnsureInputHistoryCapacity();
            if (_hasCapturedInputHistory && tick <= _latestCapturedInputTick &&
                _latestCapturedInputTick - tick <= verifiedHistoryWindowTicks)
            {
                block = _inputBlockCache[(int)(tick % (ulong)_inputBlockCache.Length)];
                if (block.captured && block.tick == tick)
                    return true;
            }

            block = default;
            return false;
        }

        private CachedInputBlock GetInputBlockForTick(ulong tick)
        {
            if (TryGetInputBlockForTick(tick, out var block))
                return block;
            throw new MissingPredictionBaselineException(
                $"Authoritative input tick {tick} was not captured or is no longer retained.");
        }

        private static void DisposeInputBlock(ref CachedInputBlock block)
        {
            block.packer?.Dispose();
            block.deltas?.Dispose();
            block = default;
        }

        private void DisposeInputBlockCache()
        {
            if (_inputBlockCache != null)
            {
                for (var i = 0; i < _inputBlockCache.Length; i++)
                    DisposeInputBlock(ref _inputBlockCache[i]);
                _inputBlockCache = null;
            }

            DisposeInputBlock(ref _inputBlockScratch);
            _latestCapturedInputTick = 0;
            _hasCapturedInputHistory = false;
        }
    }
}
