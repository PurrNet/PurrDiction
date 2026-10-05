using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class VerifiedInputTranscriptProtocolTests
    {
        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) => value.id = Packer<int>.Read(packer));
        }

        [Test]
        public void LossReorderingDuplicatesAndDelayedAcksNeverLeaveAVerifiedInputGap()
        {
            using var sender = new InputTranscriptFixture("protocol sender");
            using var receiver = new InputTranscriptFixture("protocol receiver");
            var sent = new[] { sender.AddInput(false, 700), sender.AddInput(true, 701) };
            var received = new[] { receiver.AddInput(false, 700), receiver.AddInput(true, 701) };
            var frames = new List<WrittenFrame>();
            var pending = new List<Delivery>();
            var acknowledgements = new List<(ulong due, ulong tick)>();
            var rng = new Random(731);
            ulong applied = 1;
            ulong acknowledged = 1;
            int lost = 0;
            int stale = 0;
            int recoveredGaps = 0;
            int delayedAckFrames = 0;
            try
            {
                for (ulong tick = 2; tick <= 120; tick++)
                {
                    for (var a = acknowledgements.Count - 1; a >= 0; a--)
                    {
                        if (acknowledgements[a].due > tick)
                            continue;
                        acknowledged = Math.Max(acknowledged, acknowledgements[a].tick);
                        acknowledgements.RemoveAt(a);
                    }
                    for (var identity = 0; identity < sent.Length; identity++)
                        sent[identity].Write(tick, new TrackedInput(Value(identity, tick)));
                    sender.Capture(tick);
                    var written = new WrittenFrame
                    {
                        tick = tick,
                        baseline = acknowledged,
                        bits = sender.Write(tick, acknowledged)
                    };
                    frames.Add(written);
                    if (acknowledged < applied)
                        delayedAckFrames++;

                    if (rng.Next(100) < 30)
                    {
                        lost++;
                    }
                    else
                    {
                        ulong due = tick + (ulong)rng.Next(5);
                        pending.Add(new Delivery { due = due, frame = written });
                        if (rng.Next(100) < 25)
                            pending.Add(new Delivery { due = due + 3, frame = written });
                    }

                    // Reverse arrival order for packets sharing a delivery tick so newer
                    // continuations can overtake older ones, as they can on UDP.
                    for (var d = pending.Count - 1; d >= 0; d--)
                    {
                        if (pending[d].due > tick)
                            continue;
                        var delivery = pending[d].frame;
                        pending.RemoveAt(d);
                        if (delivery.tick <= applied)
                        {
                            stale++;
                            continue;
                        }
                        if (delivery.tick > applied + 1)
                            recoveredGaps++;
                        receiver.Read(delivery.bits, delivery.tick, delivery.baseline);
                        applied = delivery.tick;
                        acknowledgements.Add((tick + 5, applied));
                        AssertContiguous(received, applied);
                    }
                }

                // Deliver the final fresh continuation even if the seeded schedule lost it.
                var final = frames[frames.Count - 1];
                if (final.tick > applied)
                {
                    receiver.Read(final.bits, final.tick, final.baseline);
                    applied = final.tick;
                }
                AssertContiguous(received, 120);
                Assert.That(applied, Is.EqualTo(120));
                Assert.That(lost, Is.GreaterThan(15), "schedule must drop frames");
                Assert.That(stale, Is.GreaterThan(10), "schedule must exercise stale/duplicate arrival");
                Assert.That(recoveredGaps, Is.GreaterThan(10), "later frames must repair missing ticks");
                Assert.That(delayedAckFrames, Is.GreaterThan(50), "ACK lag must be independent of delivery");
            }
            finally
            {
                foreach (var frame in frames)
                    frame.bits.Dispose();
            }
        }

        [TestCase(20)]
        [TestCase(60)]
        public void StalledAckRetainsEveryInputUntilHistoryRequiresACheckpoint(int tickRate)
        {
            using var sender = new InputTranscriptFixture("ACK stall sender", tickRate);
            using var receiver = new InputTranscriptFixture("ACK stall receiver", tickRate);
            var sent = new[] { sender.AddInput(false, 710), sender.AddInput(true, 711) };
            var received = new[] { receiver.AddInput(false, 710), receiver.AddInput(true, 711) };
            const ulong baseline = 100;
            ulong lastTick = baseline + sender.manager.verifiedHistoryWindowTicks;
            for (ulong tick = baseline + 1; tick <= lastTick; tick++)
            {
                foreach (var history in sent)
                    history.Write(tick, new TrackedInput(777));
                sender.Capture(tick);
                using var frame = sender.Write(tick, baseline);
                if (tick != lastTick)
                    continue; // All preceding frames in the retained interval are lost.
                receiver.Read(frame, tick, baseline);
            }

            foreach (var history in received)
            {
                Assert.That(history.Count, Is.EqualTo((int)(lastTick - baseline)));
                for (ulong tick = baseline + 1; tick <= lastTick; tick++)
                {
                    Assert.That(history.TryGet(tick, out var value), Is.True, $"missing tick {tick}");
                    Assert.That(value.id, Is.EqualTo(777));
                }
            }
            foreach (var history in sent)
                history.Write(lastTick + 1, new TrackedInput(777));
            sender.Capture(lastTick + 1);
            Assert.Throws<MissingPredictionBaselineException>(() =>
            {
                using var incomplete = sender.Write(lastTick + 1, baseline);
            }, "a continuation must not silently discard the oldest unacknowledged tick");
        }

        private static int Value(int identity, ulong tick)
            => 1000 * (identity + 1) + (int)tick;

        private static void AssertContiguous(History<TrackedInput>[] histories, ulong through)
        {
            for (var identity = 0; identity < histories.Length; identity++)
                for (ulong tick = 2; tick <= through; tick++)
                {
                    Assert.That(histories[identity].TryGet(tick, out var input), Is.True,
                        $"identity {identity}, verified through {through}: missing tick {tick}");
                    Assert.That(input.id, Is.EqualTo(Value(identity, tick)),
                        $"identity {identity}, tick {tick}: authoritative input mismatch");
                }
        }

        private sealed class WrittenFrame
        {
            internal ulong tick;
            internal ulong baseline;
            internal BitPacker bits;
        }

        private struct Delivery
        {
            internal ulong due;
            internal WrittenFrame frame;
        }
    }
}
