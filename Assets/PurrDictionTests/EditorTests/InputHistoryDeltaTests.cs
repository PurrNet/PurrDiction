using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InputHistoryDeltaTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void SparseChangesRoundTripAcrossBitAndGroupBoundaries(int alignment)
        {
            int[] lengths = { 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 55, 56, 57, 63,
                64, 65, 71, 72, 73, 127, 128, 129, 255, 256, 257 };
            foreach (int length in lengths)
            {
                var before = Pattern(length);
                var after = (byte[])before.Clone();
                after[(length - 1) / 8] ^= (byte)(1 << ((length - 1) & 7));
                using var baseline = Input(before, length, alignment, out var previous);
                using var current = Input(after, length, (alignment + 3) & 7, out var next);
                using var encoded = new BitPacker(1);
                int encodedOrigin = (alignment + 5) & 7;
                if (encodedOrigin > 0)
                    encoded.WriteBits(0x7f, (byte)encodedOrigin);
                int baselineCursor = baseline.positionInBits;
                int currentCursor = current.positionInBits;
                var untouched = (byte[])encoded.buffer.Clone();
                int byteCount = (length + 7) / 8;
                int encodedBits = ((byteCount + 7) / 8 + 1) * 8;

                bool written = InputHistoryDelta.TryWrite(encoded, previous, next);
                int rawBits = length + (length < 128 ? 8 : 16);
                Assert.That(written, Is.EqualTo(encodedBits < rawBits), $"length={length}");
                Assert.That(baseline.positionInBits, Is.EqualTo(baselineCursor));
                Assert.That(current.positionInBits, Is.EqualTo(currentCursor));
                if (!written)
                {
                    Assert.That(encoded.positionInBits, Is.EqualTo(encodedOrigin));
                    Assert.That(encoded.buffer, Is.EqualTo(untouched));
                    continue;
                }

                int end = encoded.positionInBits;
                Assert.That(end - encodedOrigin, Is.EqualTo(encodedBits));
                encoded.WriteBits(0xff, 8); // A following record must remain unread.
                encoded.ResetMode(true);
                encoded.SetBitPosition(encodedOrigin);
                using var reconstructed = new BitPacker(1);
                int outputOrigin = (alignment + 1) & 7;
                if (outputOrigin > 0)
                    reconstructed.WriteBits(0x7f, (byte)outputOrigin);
                InputHistoryDelta.Read(encoded, end, previous, reconstructed);
                Assert.That(new BitData(reconstructed, outputOrigin, length).Equals(next), Is.True,
                    $"alignment={alignment}, length={length}");
                Assert.That(reconstructed.positionInBits, Is.EqualTo(outputOrigin + length));
                Assert.That(encoded.positionInBits, Is.EqualTo(end));
                Assert.That(encoded.ReadBits(8), Is.EqualTo(0xff));
                Assert.That(reconstructed.buffer[0] & ((1 << outputOrigin) - 1),
                    Is.EqualTo((1 << outputOrigin) - 1));
            }
        }

        [Test]
        public void WireMasksOrderChangesAndZeroExtendTheFinalPartialByte()
        {
            var before = new byte[18];
            var after = new byte[18];
            after[0] = 0xab;
            after[7] = 0xcd;
            after[8] = 0xef;
            after[17] = 0xff;
            using var baseline = Input(before, 137, 3, out var previous);
            using var current = Input(after, 137, 6, out var next);
            using var encoded = new BitPacker(1);
            Assert.That(InputHistoryDelta.TryWrite(encoded, previous, next), Is.True);
            Assert.That(encoded.positionInBits, Is.EqualTo(56));
            Assert.That(encoded.ToByteData().span.ToArray(),
                Is.EqualTo(new byte[] { 0x81, 0xab, 0xcd, 0x01, 0xef, 0x02, 0x01 }));
        }

        [TestCase(16, 16, 2)]
        [TestCase(257, 257, 33)]
        [TestCase(128, 129, 1)]
        [TestCase(0, 0, 0)]
        public void UnhelpfulOrDifferentLengthDeltasLeaveDestinationUntouched(
            int previousLength, int currentLength, int changedBytes)
        {
            var before = new byte[(previousLength + 7) / 8];
            var after = new byte[(currentLength + 7) / 8];
            for (int i = 0; i < changedBytes; i++)
                after[i] = 0xff;
            using var baseline = Input(before, previousLength, 2, out var previous);
            using var current = Input(after, currentLength, 5, out var next);
            using var encoded = new BitPacker(1);
            encoded.WriteBits(0x5b, 7);
            var snapshot = (byte[])encoded.buffer.Clone();
            Assert.That(InputHistoryDelta.TryWrite(encoded, previous, next), Is.False);
            Assert.That(encoded.positionInBits, Is.EqualTo(7));
            Assert.That(encoded.buffer, Is.EqualTo(snapshot));
        }

        [Test]
        public void EveryTruncatedPatchStopsAtTheSuppliedFrameBoundary()
        {
            var before = new byte[18];
            var after = new byte[18];
            after[0] = 3;
            after[8] = 7;
            after[17] = 1;
            using var baseline = Input(before, 137, 5, out var previous);
            using var current = Input(after, 137, 2, out var next);
            using var encoded = new BitPacker(1);
            encoded.WriteBits(0x1f, 5);
            Assert.That(InputHistoryDelta.TryWrite(encoded, previous, next), Is.True);
            int end = encoded.positionInBits;
            encoded.WriteBits(ulong.MaxValue, 64);
            encoded.ResetMode(true);
            for (int boundary = 5; boundary < end; boundary++)
            {
                encoded.SetBitPosition(5);
                using var output = new BitPacker(1);
                Assert.Throws<MissingPredictionBaselineException>(() =>
                    InputHistoryDelta.Read(encoded, boundary, previous, output), $"boundary={boundary}");
                Assert.That(encoded.positionInBits, Is.LessThanOrEqualTo(boundary));
                Assert.That(output.positionInBits, Is.LessThanOrEqualTo(137));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonzeroUnusedMaskOrPartialByteBitsAreRejected(bool partialByte)
        {
            using var baseline = Input(new byte[9], 65, 0, out var previous);
            using var encoded = new BitPacker(1);
            encoded.WriteBits(0, 8); // First eight logical bytes are unchanged.
            encoded.WriteBits(partialByte ? 1UL : 2UL, 8);
            if (partialByte)
                encoded.WriteBits(0x80, 8); // Last logical byte contains only one bit.
            int end = encoded.positionInBits;
            encoded.ResetPositionAndMode(true);
            using var output = new BitPacker(1);
            Assert.Throws<MissingPredictionBaselineException>(() =>
                InputHistoryDelta.Read(encoded, end, previous, output));
            Assert.That(encoded.positionInBits, Is.LessThanOrEqualTo(end));
        }

        [Test]
        public void AppendingManyDeltasPreservesEveryBaselineAcrossBufferGrowth()
        {
            const int length = 4097;
            var expected = Pattern(length);
            using var initial = Input(expected, length, 3, out var first);
            using var payload = new BitPacker(1);
            payload.WriteBits(0x1f, 5);
            payload.WriteBitDataWithoutConsumingIt(first);
            var previous = new BitData(payload, 5, length);
            var history = new List<(BitData span, byte[] bytes)> { (previous, (byte[])expected.Clone()) };
            int grows = 0;
            for (int tick = 0; tick < 40; tick++)
            {
                expected[(tick * 37) % expected.Length] ^= 1;
                using var current = Input(expected, length, tick & 7, out var next);
                using var encoded = new BitPacker(1);
                Assert.That(InputHistoryDelta.TryWrite(encoded, previous, next), Is.True);
                int end = encoded.positionInBits;
                encoded.ResetPositionAndMode(true);
                int origin = payload.positionInBits;
                var buffer = payload.buffer;
                InputHistoryDelta.Read(encoded, end, previous, payload);
                if (!ReferenceEquals(buffer, payload.buffer))
                    grows++;
                previous = new BitData(payload, origin, length);
                Assert.That(previous.Equals(next), Is.True, $"tick={tick}");
                history.Add((previous, (byte[])expected.Clone()));
            }
            Assert.That(grows, Is.GreaterThanOrEqualTo(3));
            foreach (var saved in history)
            {
                using var input = Input(saved.bytes, length, 0, out var expectedSpan);
                Assert.That(saved.span.Equals(expectedSpan), Is.True);
            }
        }

        private static byte[] Pattern(int length)
        {
            var bytes = new byte[(length + 7) / 8];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(i * 73 + 29);
            return bytes;
        }

        private static BitPacker Input(byte[] bytes, int length, int origin, out BitData span)
        {
            var result = new BitPacker(1);
            if (origin > 0)
                result.WriteBits(0x7f, (byte)origin);
            for (int i = 0; i < bytes.Length; i++)
                result.WriteBits(bytes[i], (byte)Math.Min(8, length - i * 8));
            span = new BitData(result, origin, length);
            result.WriteBits(0xff, 8); // Dirty adjacent bits are outside the input span.
            return result;
        }
    }
}
