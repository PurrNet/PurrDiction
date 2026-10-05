using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class LagCompensationTests
    {
        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) =>
                    value.id = Packer<int>.Read(packer));
        }

        [Test]
        public void ViewOffsetRosterRepeatsCostOneBitPerTick()
        {
            const ulong first = 11, last = 30;
            int BitsFor(int players, bool changeRosterEveryTick)
            {
                using var sender = new InputTranscriptFixture("sender");
                var inputs = sender.AddInput(false, 661);
                for (ulong tick = first; tick <= last; tick++)
                {
                    inputs.Write(tick, new TrackedInput(7));
                    for (uint p = 1; p <= players; p++)
                    {
                        ulong id = changeRosterEveryTick ? p + 10 * (tick - first) : p;
                        sender.manager.RecordViewOffset(new PlayerID(id, false), tick, 40 + p);
                    }
                    sender.Capture(tick);
                }
                using var frame = sender.Write(last, first - 1);
                return frame.positionInBits;
            }

            int ticks = (int)(last - first + 1);
            int none = BitsFor(0, false);
            int repeating = BitsFor(4, false);
            int churning = BitsFor(4, true);
            TestContext.Out.WriteLine(
                $"transcript bits per tick: no offsets {none / (double)ticks:F1}, four players repeating " +
                $"{repeating / (double)ticks:F1}, four players churning {churning / (double)ticks:F1}");
            UnityEngine.Debug.Log($"[ViewOffsetTranscriptCost] none={none / (double)ticks:F1} repeating={repeating / (double)ticks:F1} churning={churning / (double)ticks:F1} bits/tick");
            // Four ids per tick are written once when the roster repeats, every tick when it churns.
            Assert.That(churning - repeating, Is.GreaterThan(ticks * 4 * 8 / 2),
                $"repeating={repeating} churning={churning}: a repeating roster must not re-send player ids");
        }

        [Test]
        public void ViewOffsetsRideTheVerifiedInputTranscriptAndSurviveRosterChanges()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var senderInputs = sender.AddInput(false, 660);
            receiver.AddInput(false, 660);
            var alice = new PlayerID(3, false);
            var bob = new PlayerID(5, false);

            for (ulong tick = 11; tick <= 14; tick++)
            {
                senderInputs.Write(tick, new TrackedInput((int)tick));
                sender.manager.RecordViewOffset(alice, tick, (uint)(10 + tick));
                if (tick != 13)
                    sender.manager.RecordViewOffset(bob, tick, (uint)(20 + tick));
                sender.Capture(tick);
            }

            using var frame = sender.Write(14, 10);
            receiver.Read(frame, 14, 10);

            for (ulong tick = 11; tick <= 14; tick++)
            {
                Assert.That(receiver.manager.TryGetViewOffset(alice, tick, out var aliceOffset), Is.True, $"alice {tick}");
                Assert.That(aliceOffset, Is.EqualTo((uint)(10 + tick)), $"alice {tick}");

                bool bobExpected = tick != 13;
                Assert.That(receiver.manager.TryGetViewOffset(bob, tick, out var bobOffset), Is.EqualTo(bobExpected), $"bob {tick}");
                if (bobExpected)
                    Assert.That(bobOffset, Is.EqualTo((uint)(20 + tick)), $"bob {tick}");
            }

            Assert.That(receiver.manager.GetLagCompensationTick(alice, 12),
                Is.EqualTo(sender.manager.GetLagCompensationTick(alice, 12)).Within(1e-12));
        }
        [Test]
        public void ViewOffsetQuantizationRoundTripsAtOneSixtyFourthTick()
        {
            Assert.That(PredictionManager.QuantizeViewOffset(0.5d), Is.EqualTo(32u));
            Assert.That(PredictionManager.DequantizeViewOffset(32u), Is.EqualTo(0.5d).Within(1e-12));
            Assert.That(PredictionManager.QuantizeViewOffset(1.25d), Is.EqualTo(80u));
            Assert.That(PredictionManager.DequantizeViewOffset(80u), Is.EqualTo(1.25d).Within(1e-12));
        }

        [Test]
        public void ViewOffsetQuantizationClampsToTheWireRange()
        {
            Assert.That(PredictionManager.QuantizeViewOffset(-1d), Is.EqualTo(0u));
            Assert.That(PredictionManager.QuantizeViewOffset(double.NaN), Is.EqualTo(0u));
            Assert.That(PredictionManager.QuantizeViewOffset(1000d), Is.EqualTo(PredictionManager.ViewOffsetMaxQuantized));
            Assert.That(PredictionManager.ViewOffsetMaxQuantized, Is.EqualTo(1023u));
        }

        [Test]
        public void ServerClampNeverRaisesAnOffset()
        {
            Assert.That(PredictionManager.ClampViewOffset(500u, 2d), Is.EqualTo(128u));
            Assert.That(PredictionManager.ClampViewOffset(100u, 2d), Is.EqualTo(100u));
            Assert.That(PredictionManager.ClampViewOffset(100u, 0d), Is.EqualTo(0u));
        }

        private static History<PredictionManager.ColliderTickSample> Map(params (ulong prediction, uint tickManager)[] entries)
        {
            var map = new History<PredictionManager.ColliderTickSample>(64);
            foreach (var (prediction, tickManager) in entries)
                map.Write(prediction, new PredictionManager.ColliderTickSample { tickManagerTick = tickManager });
            return map;
        }

        [Test]
        public void ColliderTickResolutionKeepsTheFractionOnAnExactEntry()
        {
            var map = Map((10, 110), (11, 111), (13, 113));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 11.25d, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(111.25d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionSpansASkippedTickProportionally()
        {
            var map = Map((10, 110), (11, 111), (13, 113));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 12.5d, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(112.5d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionMapsALeadJumpOntoTheSingleSampleTheViewLerpedAcross()
        {
            var map = Map((10, 110), (15, 111), (16, 112));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 12.5d, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(110.5d).Within(1e-9));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 14d, out var late), Is.True);
            Assert.That(late, Is.EqualTo(110.8d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionUsesTheNewestSampleForAPausedTick()
        {
            var map = Map((10, 110), (11, 111));
            map.Write(11, new PredictionManager.ColliderTickSample { tickManagerTick = 112 });
            map.Write(12, new PredictionManager.ColliderTickSample { tickManagerTick = 113 });
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 11.5d, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(112.5d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionExtendsBackwardsBeforeTheFirstEntry()
        {
            var map = Map((10, 110), (11, 111));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 8.5d, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(108.5d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionFollowsAHitchOffsetChange()
        {
            var map = Map((20, 120), (21, 123));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 20.75d, out var before), Is.True);
            Assert.That(before, Is.EqualTo(120.75d).Within(1e-9));
            Assert.That(PredictionManager.ResolveColliderRollbackTick(map, 21.75d, out var after), Is.True);
            Assert.That(after, Is.EqualTo(123.75d).Within(1e-9));
        }

        [Test]
        public void ColliderTickResolutionRejectsAnEmptyMap()
        {
            Assert.That(PredictionManager.ResolveColliderRollbackTick(null, 5d, out _), Is.False);
            Assert.That(PredictionManager.ResolveColliderRollbackTick(Map(), 5d, out _), Is.False);
            Assert.That(PredictionManager.ResolveColliderRollbackTick(Map((1, 1)), double.NaN, out _), Is.False);
        }

        [Test]
        public void ViewClockPresentsTheFractionBetweenLatchedTicks()
        {
            const int tickRate = 60;
            const float halfTick = 0.5f / tickRate;
            var clock = new PredictionViewClock(tickRate, 5);
            Assert.That(clock.viewTick, Is.EqualTo(5d));

            clock.Latch(6, false);
            Assert.That(clock.Advance(halfTick), Is.EqualTo(5.5d).Within(1e-6));
            Assert.That(clock.Advance(halfTick), Is.EqualTo(6d).Within(1e-6));

            clock.Latch(7, false);
            Assert.That(clock.Advance(halfTick), Is.EqualTo(6.5d).Within(1e-6));
            Assert.That(clock.viewTick, Is.EqualTo(6.5d).Within(1e-6));
        }

        [Test]
        public void ViewClockReplacesAPendingLatchLikeIdentitiesDo()
        {
            const int tickRate = 60;
            var clock = new PredictionViewClock(tickRate, 5);

            clock.Latch(6, false);
            clock.Latch(7, false);
            Assert.That(clock.hasPendingLatch, Is.True);
            Assert.That(clock.Advance(0.5f / tickRate), Is.EqualTo(6d).Within(1e-6));
            Assert.That(clock.bufferSize, Is.EqualTo(1));

            clock.Advance(0.5f / tickRate);
            Assert.That(clock.hasPendingLatch, Is.False);
            clock.Latch(8, true);
            Assert.That(clock.hasPendingLatch, Is.False);
            Assert.That(clock.Advance(0.5f / tickRate), Is.EqualTo(7d).Within(1e-6));
        }

        [Test]
        public void LagCompensationTickRewindsByTheRecordedOffset()
        {
            var managerObject = new GameObject("Lag compensation manager");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var player = new PlayerID(4, false);
                manager.RecordViewOffset(player, 20, PredictionManager.QuantizeViewOffset(0.75d));

                Assert.That(manager.GetLagCompensationTick(player, 20), Is.EqualTo(19.25d).Within(1e-9));

                Assert.That(manager.GetLagCompensationTick(player, 25), Is.EqualTo(24.25d).Within(1e-9));

                var stranger = new PlayerID(8, false);
                Assert.That(manager.GetLagCompensationTick(stranger, 30),
                    Is.EqualTo(30d - PredictionManager.DefaultViewOffsetTicks).Within(1e-9));

                Assert.That(manager.GetLagCompensationTick(null, 30), Is.EqualTo(30d));
                Assert.That(manager.GetLagCompensationTick(new PlayerID(2, true), 30), Is.EqualTo(30d));
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void LagCompensationTickNeverGoesNegative()
        {
            var managerObject = new GameObject("Lag compensation manager");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var player = new PlayerID(4, false);
                manager.RecordViewOffset(player, 1, PredictionManager.QuantizeViewOffset(3d));
                Assert.That(manager.GetLagCompensationTick(player, 1), Is.EqualTo(0d));
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }
    }
}
