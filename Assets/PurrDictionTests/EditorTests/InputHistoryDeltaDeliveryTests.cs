using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InputHistoryDeltaDeliveryTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly int[] Values = { 99, 100, 101, 101, 102, 103, 103, 104, 105 };

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Packer<TrackedInput>.RegisterWriter((packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) => value.id = Packer<int>.Read(packer));
        }

        [TestCase(10UL)]
        [TestCase(14UL)]
        public void LostPacketsAndRebasedIntervalsDecodeWithoutPreviousWireInputs(ulong baseline)
        {
            using var sender = new InputTranscriptFixture("delta sender");
            using var receiver = new InputTranscriptFixture("delta receiver");
            var sentA = sender.AddInput(false, 810);
            var sentB = sender.AddInput(true, 811);
            // Registration order is not authoritative input order.
            var receivedB = receiver.AddInput(true, 811);
            var receivedA = receiver.AddInput(false, 810);
            Capture(sender, sentA, sentB);
            AssertDeltaRepeatDelta(sender.manager);

            using var lost = sender.Write(12, 10);
            using var intermediate = sender.Write(14, 10);
            if (baseline == 14)
                receiver.Read(intermediate, 14, 10);
            receivedA.Clear();
            receivedB.Clear();

            using var later = sender.Write(18, baseline);
            receiver.Read(later, 18, baseline);
            for (ulong tick = baseline + 1; tick <= 18; tick++)
            {
                AssertInput(receivedA, tick, Value(tick, false));
                AssertInput(receivedB, tick, Value(tick, true));
            }

            // Clearing mutable histories during rollback must not invalidate any
            // full, delta, or repeated entry already staged from this packet.
            receivedA.Clear();
            receivedB.Clear();
            for (ulong tick = baseline + 1; tick <= 18; tick++)
                receiver.Apply(tick);
            AssertInput(receivedA, 18, Value(18, false));
            AssertInput(receivedB, 18, Value(18, true));
        }

        [Test]
        public void RejectedDeltaPreservesAppliedAndCapturedInputsAndNextPacketRecovers()
        {
            using var sender = new InputTranscriptFixture("delta sender");
            using var receiver = new InputTranscriptFixture("delta receiver");
            var sentA = sender.AddInput(false, 810);
            var sentB = sender.AddInput(true, 811);
            var receivedA = receiver.AddInput(false, 810);
            var receivedB = receiver.AddInput(true, 811);
            Capture(sender, sentA, sentB);
            AssertDeltaRepeatDelta(sender.manager);
            receivedA.Write(9, new TrackedInput(9001));
            receivedB.Write(9, new TrackedInput(9002));
            receiver.Capture(9);
            using var retainedBefore = receiver.Write(9, 8);

            using var malformed = sender.Write(14, 10);
            malformed.SetBitPosition(malformed.positionInBits - 1);
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Parse(malformed, 14, 10));
            Assert.That(receivedA.Count, Is.EqualTo(1));
            Assert.That(receivedB.Count, Is.EqualTo(1));
            AssertInput(receivedA, 9, 9001);
            AssertInput(receivedB, 9, 9002);
            using var retainedAfter = receiver.Write(9, 8);
            Assert.That(new BitData(retainedAfter).Equals(new BitData(retainedBefore)), Is.True,
                "rejected staging must not mutate independently captured input bytes");

            using var recovery = sender.Write(18, 10);
            receiver.Read(recovery, 18, 10);
            for (ulong tick = 11; tick <= 18; tick++)
            {
                AssertInput(receivedA, tick, Value(tick, false));
                AssertInput(receivedB, tick, Value(tick, true));
            }
            AssertInput(receivedA, 9, 9001);
            AssertInput(receivedB, 9, 9002);
        }

        [Test]
        public void RosterReorderingRetainsIdentityAddressesBeforeDeltasResume()
        {
            using var sender = new InputTranscriptFixture("delta sender");
            using var receiver = new InputTranscriptFixture("delta receiver");
            var sentA = sender.AddInput(false, 810);
            var sentB = sender.AddInput(true, 811);
            var receivedA = receiver.AddInput(false, 810);
            var receivedB = receiver.AddInput(true, 811);
            var systems = (List<PredictedIdentity>)typeof(PredictionManager)
                .GetField("_systems", Members).GetValue(sender.manager);
            for (ulong tick = 10; tick <= 18; tick++)
            {
                sentA.Write(tick, new TrackedInput(Value(tick, false)));
                sentB.Write(tick, new TrackedInput(Value(tick, true)));
                if (tick == 14)
                    systems.Reverse();
                sender.Capture(tick);
            }
            using var frame = sender.Write(18, 10);
            receiver.Read(frame, 18, 10);
            for (ulong tick = 11; tick <= 18; tick++)
            {
                AssertInput(receivedA, tick, Value(tick, false));
                AssertInput(receivedB, tick, Value(tick, true));
            }
        }

        [Test]
        public void VariableInputSizesSelectFullRecordsAndStableIntervalsResumeDeltas()
        {
            using var sender = new InputTranscriptFixture("variable delta sender");
            using var receiver = new InputTranscriptFixture("variable delta receiver");
            var sent = sender.AddInput(false, 812);
            var received = receiver.AddInput(false, 812);
            var savedWrite = Packer<TrackedInput>.WriteFunc;
            var savedDirectWrite = Packer<TrackedInput>.DirectWrite;
            var savedRead = Packer<TrackedInput>.ReadFunc;
            var savedDirectRead = Packer<TrackedInput>.DirectRead;
            try
            {
                // Registration is first-writer-wins. Temporarily replace the managed
                // delegates used by WriteFirstInput/ReadFirstInput, then restore them.
                Packer<TrackedInput>.WriteFunc = Packer<TrackedInput>.DirectWrite = (packer, value) =>
                {
                    Packer<int>.Write(packer, value.id);
                    if ((value.id & 1) != 0)
                        packer.WriteBits(0xa5, 8);
                };
                Packer<TrackedInput>.ReadFunc = Packer<TrackedInput>.DirectRead =
                    (BitPacker packer, ref TrackedInput value) =>
                    {
                        value.id = Packer<int>.Read(packer);
                        if ((value.id & 1) != 0)
                            Assert.That(packer.ReadBits(8), Is.EqualTo(0xa5));
                    };
                int[] values = { 0, 2, 4, 4, 5, 6, 8, 8, 10 };
                for (ulong tick = 10; tick <= 18; tick++)
                {
                    sent.Write(tick, new TrackedInput(values[(int)(tick - 10)]));
                    sender.Capture(tick);
                    object block = typeof(PredictionManager).GetMethod("GetInputBlockForTick", Members)
                        .Invoke(sender.manager, new object[] { tick });
                    var entries = (List<PredictionManager.CachedInputEntry>)block.GetType().GetField("entries")
                        .GetValue(block);
                    Assert.That(entries[0].bitLength, Is.EqualTo(tick == 14 ? 41 : 33),
                        $"custom serialized size at tick {tick}");
                }

                using var variable = sender.Write(18, 10);
                AssertVariableEncoding(variable, 10, values);
                receiver.Read(variable, 18, 10);
                for (ulong tick = 11; tick <= 18; tick++)
                    AssertInput(received, tick, values[(int)(tick - 10)]);

                received.Clear();
                using var stable = sender.Write(18, 14);
                AssertVariableEncoding(stable, 14, values);
                receiver.Read(stable, 18, 14);
                for (ulong tick = 15; tick <= 18; tick++)
                    AssertInput(received, tick, values[(int)(tick - 10)]);
            }
            finally
            {
                Packer<TrackedInput>.WriteFunc = savedWrite;
                Packer<TrackedInput>.DirectWrite = savedDirectWrite;
                Packer<TrackedInput>.ReadFunc = savedRead;
                Packer<TrackedInput>.DirectRead = savedDirectRead;
            }
        }

        private static void AssertVariableEncoding(BitPacker frame, ulong baseline, int[] values)
        {
            int end = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That((uint)Packer<PackedUInt>.Read(frame), Is.EqualTo(18UL - baseline));
            for (ulong tick = baseline + 1; tick <= 18; tick++)
            {
                int value = values[(int)(tick - 10)];
                bool afterFirst = tick > baseline + 1;
                Assert.That((uint)Packer<PackedUInt>.Read(frame), Is.EqualTo(1));
                if (afterFirst)
                    Assert.That(Packer<bool>.Read(frame), Is.True, "same identity roster");
                Assert.That((uint)Packer<PackedUInt>.Read(frame), Is.Zero, "view offset count");
                bool repeats = afterFirst && Packer<bool>.Read(frame);
                Assert.That(repeats, Is.EqualTo(afterFirst && value == values[(int)(tick - 11)]));
                bool delta = afterFirst && !repeats && Packer<bool>.Read(frame);
                if (afterFirst && !repeats)
                    Assert.That(delta, Is.EqualTo((value & 1) == (values[(int)(tick - 11)] & 1)),
                        $"tick {tick}: a size change falls back to a full record, stable sizes use the delta");
                if (afterFirst && !repeats && !delta)
                    Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                frame.SkipBits((8 - frame.positionInBits % 8) % 8);
                if (repeats)
                    continue;
                if (delta)
                {
                    using var previous = BitPackerPool.Get();
                    previous.WriteBit(true);
                    Packer<TrackedInput>.Write(previous, new TrackedInput(values[(int)(tick - 11)]));
                    using var decoded = BitPackerPool.Get();
                    InputHistoryDelta.Read(frame, end, new BitData(previous), decoded);
                    decoded.ResetPositionAndMode(true);
                    Assert.That(decoded.ReadBit(), Is.True);
                    Assert.That(Packer<TrackedInput>.Read(decoded).id, Is.EqualTo(value));
                }
                else
                {
                    if (!afterFirst)
                    {
                        Assert.That(Packer<PredictedComponentID>.Read(frame),
                            Is.EqualTo(new PredictedComponentID(new PredictedObjectID(812), 0)));
                        Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                    }
                    int length = (int)(uint)Packer<PackedUInt>.Read(frame);
                    Assert.That(length, Is.EqualTo(33 + ((value & 1) != 0 ? 8 : 0)));
                    int origin = frame.positionInBits;
                    Assert.That(frame.ReadBit(), Is.True);
                    Assert.That(Packer<TrackedInput>.Read(frame).id, Is.EqualTo(value));
                    Assert.That(frame.positionInBits, Is.EqualTo(origin + length));
                }
            }
            Assert.That(frame.positionInBits, Is.EqualTo(end));
        }

        private static void Capture(InputTranscriptFixture sender, History<TrackedInput> first,
            History<TrackedInput> second)
        {
            for (ulong tick = 10; tick <= 18; tick++)
            {
                first.Write(tick, new TrackedInput(Value(tick, false)));
                second.Write(tick, new TrackedInput(Value(tick, true)));
                sender.Capture(tick);
            }
        }

        private static int Value(ulong tick, bool second) => Values[(int)(tick - 10)] + (second ? 65536 : 0);

        private static void AssertInput(History<TrackedInput> history, ulong tick, int expected)
        {
            Assert.That(history.TryGet(tick, out var input), Is.True, $"missing tick {tick}");
            Assert.That(input.id, Is.EqualTo(expected), $"tick {tick}");
        }

        private static void AssertDeltaRepeatDelta(PredictionManager manager)
        {
            for (ulong tick = 12; tick <= 14; tick++)
            {
                object block = typeof(PredictionManager).GetMethod("GetInputBlockForTick", Members)
                    .Invoke(manager, new object[] { tick });
                var entries = (List<PredictionManager.CachedInputEntry>)block.GetType().GetField("entries")
                    .GetValue(block);
                foreach (var entry in entries)
                {
                    Assert.That(entry.repeatsPrevious, Is.EqualTo(tick == 13));
                    if (tick == 13)
                        Assert.That(entry.deltaLength, Is.Zero);
                    else
                        Assert.That(entry.deltaLength, Is.GreaterThan(0));
                }
            }
        }
    }
}
