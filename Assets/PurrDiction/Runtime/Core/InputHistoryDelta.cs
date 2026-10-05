using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal static class InputHistoryDelta
    {
        internal static bool TryWrite(BitPacker destination, in BitData previous, in BitData current)
        {
            int length = (int)previous.bitLength.value;
            if (length != (int)current.bitLength.value)
                return false;

            int rawBits = length + PrefixBits(length);
            int bytes = (length - 1) / 8 + 1;
            int encodedBits = ((bytes - 1) / 8 + 1) * 8;
            if (encodedBits >= rawBits)
                return false;
            for (int i = 0; i < bytes; i++)
            {
                if (PeekByte(previous, i, length) == PeekByte(current, i, length))
                    continue;
                encodedBits += 8;
                if (encodedBits >= rawBits)
                    return false;
            }

            for (int first = 0; first < bytes; first += 8)
            {
                int count = Math.Min(8, bytes - first);
                byte mask = 0;
                for (int i = 0; i < count; i++)
                {
                    if (PeekByte(previous, first + i, length) != PeekByte(current, first + i, length))
                        mask |= (byte)(1 << i);
                }
                destination.WriteBits(mask, 8);
                for (int i = 0; i < count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        destination.WriteBits(PeekByte(current, first + i, length), 8);
                }
            }
            return true;
        }

        internal static void Read(BitPacker source, int sourceEndBit, in BitData previous,
            BitPacker destination)
        {
            int length = (int)previous.bitLength.value;
            int bytes = (length - 1) / 8 + 1;
            for (int first = 0; first < bytes; first += 8)
            {
                int count = Math.Min(8, bytes - first);
                byte mask = ReadByte(source, sourceEndBit);
                if ((mask >> count) != 0)
                    throw new MissingPredictionBaselineException("Invalid authoritative input delta mask.");

                for (int i = 0; i < count; i++)
                {
                    int index = first + i;
                    int bits = Math.Min(8, length - index * 8);
                    byte value = (mask & (1 << i)) != 0
                        ? ReadByte(source, sourceEndBit)
                        : PeekByte(previous, index, length);
                    if ((value >> bits) != 0)
                        throw new MissingPredictionBaselineException("Invalid authoritative input delta padding.");
                    destination.WriteBits(value, (byte)bits);
                }
            }
        }

        private static int PrefixBits(int length)
        {
            int bits = 8;
            for (int remaining = length >> 7; remaining != 0; remaining >>= 7)
                bits += 8;
            return bits;
        }

        private static byte ReadByte(BitPacker source, int endBit)
        {
            if (endBit - source.positionInBits < 8)
                throw new MissingPredictionBaselineException("Truncated authoritative input delta.");
            return (byte)source.ReadBits(8);
        }

        private static byte PeekByte(in BitData data, int index, int length)
        {
            int position = (int)data.bitOrigin.value + index * 8;
            int bytePosition = position >> 3;
            int shift = position & 7;
            int bits = Math.Min(8, length - index * 8);
            var buffer = data.packer.buffer;
            int value = buffer[bytePosition] >> shift;
            if (shift + bits > 8)
                value |= buffer[bytePosition + 1] << (8 - shift);
            return (byte)(value & ((1 << bits) - 1));
        }
    }
}
