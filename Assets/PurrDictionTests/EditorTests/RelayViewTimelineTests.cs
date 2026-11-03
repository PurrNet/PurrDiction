using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class RelayViewTimelineTests
    {
        private const double Snap = 4d;

        private static double Next(double offset, double lag, double windowMax, double elapsed = 1d)
            => PredictionManager.NextRelayViewOffset(offset, lag, windowMax, elapsed, Snap);

        [Test]
        public void ALateFrameHoldsTheViewByGrowingTheOffsetToTheLag()
        {
            Assert.That(Next(3d, 5.5d, 5.5d), Is.EqualTo(5.5d));
        }

        [Test]
        public void OffsetGlidesTowardTheWindowMaxAtTheGlideRate()
        {
            double rate = PredictionManager.RelayViewOffsetGlideRate;

            Assert.That(Next(6d, 2d, 4d), Is.EqualTo(6d - rate).Within(1e-9));
            Assert.That(Next(4d, 2d, 6d, 2d), Is.EqualTo(4d + 2d * rate).Within(1e-9));
        }

        [Test]
        public void OffsetStopsAtTheTarget()
        {
            Assert.That(Next(4.01d, 2d, 4d), Is.EqualTo(4d));
            Assert.That(Next(3.99d, 2d, 4d), Is.EqualTo(4d));
            Assert.That(Next(4d, 2d, 4d), Is.EqualTo(4d));
            Assert.That(Next(6d, 2d, 4d, -1d), Is.EqualTo(6d));
        }

        [Test]
        public void AnExcessLeftByAStallSnapsInsteadOfGliding()
        {
            Assert.That(Next(30d, 3d, 4d), Is.EqualTo(4d));
            Assert.That(Next(30d, 3d, 2d), Is.EqualTo(3d));
        }

        /// <summary>
        /// Frames sent once per server tick arrive with jitter, so each local tick applies zero,
        /// one or several of them, and an earlier stall leaves the offset far too large. The
        /// presented server tick must never move backwards, never pass the newest verified tick,
        /// and advance at one tick per tick within the glide rate apart from rare late frames.
        /// </summary>
        [Test]
        public void JitteredArrivalsPresentServerTicksAtASteadyRate()
        {
            var random = new Random(1234);
            const ulong baseLead = 6;
            const double viewLag = 1d;
            double offset = 30d; // left over from a stall
            ulong verified = 0;
            var lags = new double[PredictionManager.RelayViewLagWindow];
            int cursor = 0;
            double lastPresented = double.NaN;
            int steps = 0, offRate = 0, holds = 0;

            for (ulong local = 10; local < 6000; local++)
            {
                // Server tick t's frame lands on local tick t + baseLead + jitter(0..4).
                ulong newest = verified;
                for (ulong t = verified + 1; t + baseLead <= local; t++)
                {
                    ulong jitter = (ulong)((t * 2654435761UL >> 7) % 5);
                    if (t + baseLead + jitter <= local) newest = t;
                    else break;
                }
                if (random.Next(10) == 0) newest = verified; // an occasional late batch
                verified = newest;
                if (verified == 0)
                    continue;

                // One render frame per tick.
                double lag = Math.Max(0d, local - viewLag - verified);
                cursor = (cursor + 1) % lags.Length;
                lags[cursor] = lag;
                double windowMax = 0d;
                foreach (var l in lags)
                    windowMax = Math.Max(windowMax, l);
                bool held = lag > offset + 1e-9;
                offset = Next(offset, lag, windowMax);

                double presented = local - viewLag - offset;
                Assert.That(presented, Is.LessThanOrEqualTo((double)verified), $"presented a state not received yet at {local}");
                if (!double.IsNaN(lastPresented) && local > 200)
                {
                    steps++;
                    double speed = presented - lastPresented;
                    Assert.That(speed, Is.GreaterThanOrEqualTo(0d), $"view moved backwards at {local}");
                    if (held) holds++;
                    else if (Math.Abs(speed - 1d) > PredictionManager.RelayViewOffsetGlideRate + 1e-9)
                        offRate++;
                }

                lastPresented = presented;
            }

            Assert.That(steps, Is.GreaterThan(5000));
            Assert.That(offRate, Is.Zero, "presented ticks should advance one per tick within the glide rate");
            Assert.That(holds, Is.LessThan(steps / 50), "late frames should rarely hold the view");
            Assert.That(offset, Is.LessThan(12d), "the stall's offset should be gone");
        }
    }
}
