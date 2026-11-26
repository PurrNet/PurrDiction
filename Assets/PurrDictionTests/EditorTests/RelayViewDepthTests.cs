using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// After a stall the relay clock (<see cref="RelayViewClock"/>) plays the backlog through rather than
    /// jumping it, so relayed identities are presented further behind their newest sample than the predicted
    /// view ever is. Their view buffers must still hold every sample that view has not reached. Otherwise the
    /// anchor stays on the last sample before the stall, and every field the lerp does not blend (phases,
    /// counters, ids) freezes until the clock catches up.
    /// </summary>
    public sealed class RelayViewDepthTests
    {
        private const int TickRate = 30;

        // A relayed state's phase (an enum or ulong field): PurrNet's generated lerp keeps the first
        // operand's value.
        private readonly struct Probe : IDisposable
        {
            public readonly ulong phase;

            public Probe(ulong phase) => this.phase = phase;

            public void Dispose() { }
        }

        private static Probe Lerp(Probe from, Probe to, float t) => from;

        private static PredictedViewBuffer<Probe> CreateIdentityBuffer()
            => PredictionManager.CreateViewBuffer<Probe>(Lerp, 0, new Probe(0), TickRate);

        // After the frame for server tick S a relayed identity holds the state entering S + 1: the
        // stamp TryStampRelayedView gives its sample, and the newest tick the relay clock may present.
        private static ulong RelayedStateTick(ulong verified) => verified + 1;

        [Test]
        public void AViewAsFarBehindAsTheOffsetMayTrailStillPresentsEverySample()
        {
            var buffer = CreateIdentityBuffer();

            // As far behind its newest sample as the relay clock may run, plus the tick of slack the
            // capacity keeps.
            ulong behind = PredictionManager.RelayViewMaxExcessTicks + 1;
            for (ulong tick = 1; tick <= 300; tick++)
            {
                buffer.Add(tick, new Probe(tick));
                if (tick < behind)
                    continue;

                ulong present = tick - behind;
                Assert.That(buffer.Sample(present).phase, Is.EqualTo(present), $"newest sample {tick}");
            }
        }

        /// <summary>
        /// The relay pipeline frame by frame, at <paramref name="framesPerTick"/> render frames per
        /// local tick. Server frames are held back for <paramref name="stallTicks"/> ticks; then the
        /// backlog is applied one frame per render frame, so every server tick becomes its own
        /// sample. The relay clock is fed and advanced as <c>PredictionManager</c> feeds it, and the
        /// view samples at it. Every frame the drawn phase must be that of the newest sample at or
        /// before the relay clock.
        /// </summary>
        [TestCase(25, 2)]
        [TestCase(100, 4)] // longer than the view buffers reach: the clock is held to their depth
        public void DiscreteStateFollowsTheRelayClockThroughAStall(int stallTicks, int framesPerTick)
        {
            const ulong lead = 6; // the frame for server tick t is due on local tick t + lead
            const ulong stallAt = 300;

            var buffer = CreateIdentityBuffer();
            var clock = new RelayViewClock(TickRate);
            ulong verified = 0, lastDrawn = 0;
            double maxExcess = 0d;

            for (int frame = framesPerTick; frame < 1200 * framesPerTick; frame++)
            {
                ulong local = (ulong)(frame / framesPerTick);
                bool held = local >= stallAt && local < stallAt + (ulong)stallTicks;
                ulong due = local > lead ? local - lead : 0;
                if (!held && due > verified)
                {
                    verified++;
                    ulong stamp = RelayedStateTick(verified);
                    buffer.Add(stamp, new Probe(stamp));
                    clock.RecordArrival(stamp);
                }

                if (verified == 0)
                    continue;

                ulong newest = RelayedStateTick(verified);
                clock.Advance(1d / framesPerTick, newest, (double)frame / framesPerTick, double.PositiveInfinity);
                double present = clock.presentedTick;
                maxExcess = Math.Max(maxExcess, newest - present);

                // Stamps run 2, 3, ... up to the newest, so the newest at or before the relay clock is
                // the clock's whole tick, capped at the newest; before the first stamp the view shows
                // the initial state (phase 0).
                ulong expected = present < 2d ? 0 : Math.Min((ulong)present, newest);
                ulong drawn = buffer.Sample(present).phase;
                Assert.That(drawn, Is.EqualTo(expected),
                    $"frame {frame}: relay clock {present:F2}, newest {newest}");
                Assert.That(drawn, Is.GreaterThanOrEqualTo(lastDrawn), $"frame {frame}: the view went backwards");
                lastDrawn = drawn;
            }

            Assert.That(maxExcess, Is.GreaterThan(PredictionManager.GetPredictedViewBufferSize(TickRate)),
                "the stall should leave the relay clock further behind its newest sample than the predicted view's buffer reaches");
            Assert.That(maxExcess, Is.LessThanOrEqualTo(PredictionManager.RelayViewMaxExcessTicks + 1e-9));
        }

        [Test]
        public void ABufferAllocatesTheRelayedDepthOnlyWhenSamplesQueueThatDeep()
        {
            var buffer = CreateIdentityBuffer();
            int reserved = buffer.allocatedSlots;
            Assert.That(reserved, Is.EqualTo(PredictionManager.GetPredictedViewBufferSize(TickRate) + 1));

            // A predicted view: the clock follows the newest sample a few ticks behind.
            for (ulong tick = 1; tick <= 200; tick++)
            {
                buffer.Add(tick, new Probe(tick));
                buffer.Sample(tick - 3d);
            }

            Assert.That(buffer.allocatedSlots, Is.EqualTo(reserved), "a predicted view never needs more");

            // A relayed view held back by a stall queues up to the capacity, and grows no further.
            for (ulong tick = 201; tick <= 400; tick++)
                buffer.Add(tick, new Probe(tick));

            Assert.That(buffer.Count, Is.EqualTo(buffer.capacity));
            Assert.That(buffer.allocatedSlots, Is.EqualTo(buffer.capacity + 1));
        }
    }
}
