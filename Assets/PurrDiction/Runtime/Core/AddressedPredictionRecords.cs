using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    // Component IDs decouple records from local system order; bit lengths let receivers
    // skip unknown records without byte-aligning their payloads.
    internal static class AddressedPredictionRecords
    {
        internal delegate void ReadRecord(
            PredictedComponentID id,
            bool isFullState,
            BitPacker payload,
            int payloadBitCount);

        // The source is already past the failed record, so callers can continue the section.
        internal delegate void RecordFailure(
            PredictedComponentID id,
            Exception error,
            int declaredBits,
            int consumedBits);

        // A leading BitPacker would make codegen discover this as a global serializer.
        internal static void WriteSectionCount(int count, BitPacker packer)
        {
            Packer<PackedUInt>.Write(packer, (uint)count);
        }

        public static void WriteRecord(
            BitPacker destination,
            PredictedComponentID id,
            bool isFullState,
            BitPacker payload)
        {
            int payloadBits = payload.positionInBits;
            Packer<PredictedComponentID>.Write(destination, id);
            Packer<bool>.Write(destination, isFullState);
            Packer<PackedUInt>.Write(destination, (uint)payloadBits);
            destination.WriteBitsWithoutConsumingIt(payload, payloadBits);
        }

        internal static void ReadSection(ReadRecord readRecord, BitPacker source, RecordFailure onRecordFailure = null)
        {
            PackedUInt count = default;
            Packer<PackedUInt>.Read(source, ref count);

            for (uint i = 0; i < count.value; i++)
                ReadOne(readRecord, source, onRecordFailure);
        }

        // Gap replay needs the trailing event transcript before ordinary states are decoded.
        internal static void SkipSection(BitPacker source, int endBit, uint maximumRecords = uint.MaxValue)
        {
            if (source.positionInBits >= endBit)
                throw new InvalidOperationException("Missing addressed section header.");
            uint count = Packer<PackedUInt>.Read(source);
            if (source.positionInBits > endBit || count > maximumRecords ||
                count > (uint)(endBit - source.positionInBits))
                throw new InvalidOperationException("Addressed section count exceeds its frame.");
            for (uint i = 0; i < count; i++)
            {
                Packer<PredictedComponentID>.Read(source);
                Packer<bool>.Read(source);
                uint bits = Packer<PackedUInt>.Read(source);
                int next = checked(source.positionInBits + checked((int)bits));
                if (next > endBit)
                    throw new InvalidOperationException("Addressed record exceeds its frame.");
                source.SetBitPosition(next);
            }
        }

        internal static void ReadOne(ReadRecord readRecord, BitPacker source, RecordFailure onRecordFailure = null)
        {
            PredictedComponentID id = default;
            Packer<PredictedComponentID>.Read(source, ref id);

            bool isFullState = default;
            Packer<bool>.Read(source, ref isFullState);

            PackedUInt payloadBits = default;
            Packer<PackedUInt>.Read(source, ref payloadBits);

            int payloadLength = checked((int)payloadBits.value);
            using var boundedPayload = BitPackerPool.Get();
            boundedPayload.WriteBits(source, payloadLength);
            boundedPayload.ResetPositionAndMode(true);

            Exception failure = null;

            try
            {
                readRecord?.Invoke(id, isFullState, boundedPayload, payloadLength);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            int consumedBits = boundedPayload.positionInBits;

            if (failure == null && consumedBits != payloadLength && consumedBits != 0)
            {
                failure = new InvalidOperationException(
                    $"Prediction record {id} consumed {consumedBits} bits " +
                    $"of its declared {payloadLength}-bit payload.");
            }

            if (failure == null)
                return;

            if (onRecordFailure != null)
            {
                onRecordFailure(id, failure, payloadLength, consumedBits);
                return;
            }

            throw new InvalidOperationException(
                $"Failed to read prediction record {id}: {failure.Message}",
                failure);
        }
    }
}
