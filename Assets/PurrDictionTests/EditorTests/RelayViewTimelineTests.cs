using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Relayed identities are presented on their own clock (<see cref="RelayViewClock"/>), steered toward the
    /// newest verified state less a cushion that covers how late most recent frames landed. A simulated link
    /// drives it: the frame for server tick t leaves at local time t, lands after the scenario's delay, and
    /// frames apply in order, as PredictionManager applies them: in a render frame before the clock
    /// advances for it. Two render frames per tick unless a test says otherwise.
    /// </summary>
    public sealed class RelayViewTimelineTests
    {
        private const int TickRate = 30;
        private const double Frame = 0.5d;
        private const double NormalLateness = 3d;

        private sealed class Link
        {
            private readonly Func<ulong, double> _lands;
            private readonly double _frame;
            private double _lastLanded = double.NegativeInfinity;
            private ulong _next;

            public readonly RelayViewClock clock;
            public double now;
            public double viewTick;
            public bool viewClockPaused;
            public double cap = double.PositiveInfinity;
            public ulong head;
            public double step;

            public Link(Func<ulong, double> lands, ulong firstTick = 100, RelayViewClock clock = null, double frame = Frame)
            {
                _lands = lands;
                _frame = frame;
                _next = firstTick;
                now = firstTick;
                viewTick = now + 4d;
                this.clock = clock ?? new RelayViewClock(TickRate);
            }

            public double presented => clock.presentedTick;

            // How far the presented tick trails local time, which the server's ticks keep pace with.
            public double delay => now - clock.presentedTick;

            public bool waiting => step < 0.25d * _frame;

            public void Step()
            {
                now += _frame;
                if (!viewClockPaused)
                    viewTick += _frame;

                while (true)
                {
                    double at = Math.Max(_lastLanded, _lands(_next));
                    if (at > now)
                        break;

                    _lastLanded = at;
                    clock.RecordArrival(_next);
                    head = _next++;
                }

                double before = clock.presentedTick;
                clock.Advance(_frame, head, viewTick, cap);
                step = clock.presentedTick - before;
            }

            public void RunUntil(double time)
            {
                while (now < time - 1e-9)
                    Step();
            }
        }

        // Frames land 3 to 5 ticks after they leave.
        private static double Jittered(ulong tick) => tick + NormalLateness + (double)((tick * 2654435761UL >> 7) % 3);

        // Frames land anywhere from 3 to 5 ticks after they leave, not only on whole ticks.
        private static double JitteredFinely(ulong tick) =>
            tick + NormalLateness + (double)((tick * 2654435761UL >> 7) % 1000) / 500d;

        // From tick first on, every `every` ticks the link stops for `length` ticks; the frames sent meanwhile
        // land together when it resumes.
        private static Func<ulong, double> Bursts(ulong first, ulong every, ulong length) => tick =>
        {
            if (tick >= first)
            {
                ulong into = (tick - first) % every;
                if (into < length)
                    return tick - into + length + NormalLateness;
            }

            return tick + NormalLateness;
        };

        private const ulong Once = ulong.MaxValue / 2;

        private static int CountWaits(Link link, double until)
        {
            int waits = 0;
            while (link.now < until - 1e-9)
            {
                link.Step();
                if (link.waiting)
                    waits++;
            }

            return waits;
        }

        [Test]
        public void NeverPresentsAStateNotReceivedYetNorMovesBackwards()
        {
            var bursts = Bursts(200, 140, 9);
            var link = new Link(tick => bursts(tick) + (double)((tick * 2654435761UL >> 7) % 3));
            link.RunUntil(110);

            for (int i = 0; i < 6000; i++)
            {
                link.Step();
                Assert.That(link.presented, Is.LessThanOrEqualTo(link.head + 1e-9), $"presented a state not received yet at {link.now}");
                Assert.That(link.step, Is.GreaterThanOrEqualTo(-1e-9), $"moved backwards at {link.now}");
            }
        }

        [Test]
        public void OnASteadyLinkTheViewRunsAtOneTickPerTickJustBehindTheLatestFrames()
        {
            var link = new Link(Jittered);
            link.RunUntil(100 + 10 * TickRate);

            while (link.now < 100 + 30 * TickRate)
            {
                link.Step();
                Assert.That(link.waiting, Is.False, $"waited for a frame at {link.now}");
                Assert.That(link.step / Frame, Is.EqualTo(1d).Within(0.02d), $"off speed at {link.now}");
                // Frames land at most 5 ticks late; trailing more than 3 ticks beyond that is delay nothing needs.
                Assert.That(link.delay, Is.LessThanOrEqualTo(5d + 3d), $"needlessly far back at {link.now}");
            }
        }

        [TestCase(0.5d)] // 60 fps at 30 Hz
        [TestCase(1d)] // 30 fps
        [TestCase(1.5d)] // 20 fps
        [TestCase(2d)] // 15 fps
        public void AtAnyFrameRateASteadyLinkRunsTheViewAtOneTickPerTick(double frameTicks)
        {
            // A frame applied during a render frame is first shown in that frame's view, so how late it
            // was is measured against the time the view advances to, not the previous frame's.
            var link = new Link(JitteredFinely, frame: frameTicks);
            link.RunUntil(100 + 10 * TickRate);

            while (link.now < 100 + 30 * TickRate)
            {
                link.Step();
                Assert.That(link.step / frameTicks, Is.EqualTo(1d).Within(0.02d), $"off speed at {link.now}");
            }
        }

        [Test]
        public void OnAJitterFreeLinkTheViewTrailsOnlyWhatInterpolationNeeds()
        {
            // Every frame lands exactly 3 ticks after it leaves. The view needs the sample after the one it
            // shows, so it trails local time by 3 + 1 ticks; any more is a margin no late frame calls for.
            var link = new Link(tick => tick + NormalLateness);
            link.RunUntil(100 + 10 * TickRate);

            while (link.now < 100 + 20 * TickRate)
            {
                link.Step();
                Assert.That(link.waiting, Is.False, $"waited for a frame at {link.now}");
                Assert.That(link.delay, Is.LessThanOrEqualTo(NormalLateness + 1d + 0.1d), $"needless margin at {link.now}");
            }
        }

        [Test]
        public void AtFifteenFramesASecondTheViewTrailsWhatItNeedsAndNoLess()
        {
            // Frames land on whole ticks, 3 after they leave; render frames come every 2 ticks, so a frame
            // landing between two is first seen a tick later. The view trails by the 4 ticks it takes a frame
            // to be seen, plus the tick it interpolates across: closer, and it would run into frames not
            // seen yet.
            var link = new Link(tick => tick + NormalLateness, frame: 2d);
            link.RunUntil(100 + 10 * TickRate);

            while (link.now < 100 + 20 * TickRate)
            {
                link.Step();
                Assert.That(link.delay, Is.InRange(4.9d, 5.4d), $"at {link.now}");
            }
        }

        [Test]
        public void AfterABurstTheViewCatchesUpByPlayingFasterInsteadOfJumping()
        {
            // The frames sent during ticks 400..408 land together at 412.
            var link = new Link(Bursts(400, Once, 9));
            link.RunUntil(399);
            double settled = link.delay;
            bool playedFaster = false;

            while (link.now < 412 + 3 * TickRate)
            {
                link.Step();
                Assert.That(link.step, Is.LessThanOrEqualTo(1.5d * Frame), $"jumped at {link.now}");
                if (link.now > 412 && link.now < 420 && link.step > 1.1d * Frame)
                    playedFaster = true;
            }

            Assert.That(playedFaster, Is.True, "the backlog should play through faster than real time");
            Assert.That(link.delay, Is.EqualTo(settled).Within(1d), "three seconds on, the delay before the burst");
        }

        [Test]
        public void ARareBurstStopsDelayingTheViewWithinASecond()
        {
            // A 6-tick burst every 200 ticks: fewer late frames than the coverage leaves out.
            var link = new Link(Bursts(400, 200, 6));
            link.RunUntil(399);
            double settled = link.delay;

            for (ulong burst = 400; burst < 1800; burst += 200)
            {
                link.RunUntil(burst + 6 + NormalLateness + TickRate);
                Assert.That(link.delay, Is.LessThanOrEqualTo(settled + 1d), $"a second after the burst at {burst}");
            }
        }

        [Test]
        public void FrequentBurstsRaiseTheCushionUntilTheyBarelyHoldTheView()
        {
            // A 6-tick burst every 45 ticks (1.5 s).
            var link = new Link(Bursts(400, 45, 6));
            link.RunUntil(400);
            int first = CountWaits(link, 445);

            link.RunUntil(400 + 45 * 7);
            int later = 0, bursts = 0;
            for (ulong start = 400 + 45 * 7; start < 400 + 45 * 17; start += 45)
            {
                later += CountWaits(link, start + 45);
                bursts++;
            }

            Assert.That(first, Is.GreaterThanOrEqualTo(8), "with no late frames behind it the first burst holds the view");
            Assert.That((double)later / bursts, Is.LessThanOrEqualTo(first / 2d), "later bursts should mostly be ridden out");
        }

        [Test]
        public void NeverAimsFurtherBackThanTheServerRewindsRelayedObjects()
        {
            // 15-tick bursts every 90 ticks: enough frames land late that covering them would hold the view more
            // than 16 ticks behind the view clock.
            double Behind(double cap)
            {
                var link = new Link(Bursts(400, 90, 15)) { cap = cap };
                double worst = 0d;
                for (ulong start = 400 + 90 * 4; start < 400 + 90 * 12; start += 90)
                {
                    link.RunUntil(start); // 72 ticks after the last backlog landed
                    worst = Math.Max(worst, link.viewTick - link.presented);
                }

                return worst;
            }

            Assert.That(Behind(double.PositiveInfinity), Is.GreaterThan(16d), "the scenario should want more than the cap");
            // Settled, it stays inside the cap with room to spare, so easing toward its target does not cross it.
            Assert.That(Behind(15d), Is.LessThanOrEqualTo(14.5d));
        }

        [Test]
        public void RelayedObjectsKeepMovingWhileTheViewClockPauses()
        {
            var link = new Link(Jittered);
            link.RunUntil(400);

            // The prediction clock takes back 15 ticks; the server's timeline does not stop.
            link.viewClockPaused = true;
            while (link.now < 415)
            {
                link.Step();
                Assert.That(link.step / Frame, Is.EqualTo(1d).Within(0.02d), $"relayed objects slowed at {link.now}");
            }
        }

        [Test]
        public void AfterAServerStallDropsTicksTheViewSettlesOnTheShiftedTimeline()
        {
            // The server stops for 30 ticks at 400, sends 400..405 at once, and runs on 25 ticks behind its old
            // schedule: every later frame lands 28 ticks after its tick.
            var link = new Link(tick => tick < 400
                ? tick + NormalLateness
                : Math.Max(tick + 25 + NormalLateness, 430 + NormalLateness));
            link.RunUntil(433 + 2 * TickRate);

            while (link.now < 433 + 3 * TickRate)
            {
                link.Step();
                Assert.That(link.waiting, Is.False, $"waited for a frame at {link.now}");
                Assert.That(link.step / Frame, Is.EqualTo(1d).Within(0.02d), $"off speed at {link.now}");
                Assert.That(link.delay, Is.LessThanOrEqualTo(28d + 3d), $"needlessly far back at {link.now}");
            }
        }

        [Test]
        public void TheCatchUpNeverRunsFasterThanItsSettingAllows()
        {
            // The frames sent during ticks 400..408 land together at 412.
            var link = new Link(Bursts(400, Once, 9), clock: new RelayViewClock(TickRate) { maxCatchUp = 0.1d });
            link.RunUntil(399);
            bool caughtUp = false;

            while (link.now < 412 + 6 * TickRate)
            {
                link.Step();
                Assert.That(link.step, Is.LessThanOrEqualTo(1.1d * Frame + 1e-9), $"too fast at {link.now}");
                caughtUp |= link.step > 1.05d * Frame;
            }

            Assert.That(caughtUp, Is.True, "the backlog should still play through faster than real time");
        }

        [Test]
        public void WhenFramesStartLandingLaterTheViewSlowsToMakeRoomForThem()
        {
            // From tick 400 on frames land 1.5 ticks later. The target moves back; the view must slow down to it,
            // or it keeps running into frames that have not landed.
            var link = new Link(tick => tick < 400 ? tick + NormalLateness : tick + NormalLateness + 1.5d);
            link.RunUntil(400 + 3 * TickRate);

            while (link.now < 400 + 6 * TickRate)
            {
                link.Step();
                Assert.That(link.waiting, Is.False, $"waited for a frame at {link.now}");
            }
        }

        [Test]
        public void ACoverageOfEveryFrameRidesOutBurstsTheWindowHasSeen()
        {
            // A 6-tick burst every 200 ticks: 95 % coverage leaves them out, full coverage keeps a cushion for them.
            int LaterWaits(double coverage)
            {
                var link = new Link(Bursts(400, 200, 6), clock: new RelayViewClock(TickRate) { coverage = coverage });
                link.RunUntil(600);
                return CountWaits(link, 1400);
            }

            Assert.That(LaterWaits(0.95d), Is.GreaterThan(10), "95 % coverage should still stop for each burst");
            Assert.That(LaterWaits(1d), Is.Zero);
        }

        [Test]
        public void NeverTrailsTheNewestStateFurtherThanTheViewBuffersHold()
        {
            // A 200-tick outage: the frames sent during it land together.
            var link = new Link(Bursts(400, Once, 200));
            link.RunUntil(390);

            while (link.now < 900)
            {
                link.Step();
                Assert.That(link.head - link.presented,
                    Is.LessThanOrEqualTo(PredictionManager.RelayViewMaxExcessTicks + 1e-9), $"too far behind at {link.now}");
            }
        }

        [Test]
        public void AResetStartsTheNextSessionFromItsOwnFrames()
        {
            var first = new Link(Jittered, 100);
            first.RunUntil(100 + 20 * TickRate);
            first.clock.Reset();

            // A new session whose server ticks run far ahead of the last one's.
            var second = new Link(Jittered, 5000, first.clock);
            second.RunUntil(5000 + 2 * TickRate);
            for (int i = 0; i < 4 * TickRate; i++)
            {
                second.Step();
                Assert.That(second.waiting, Is.False, $"waited for a frame at {second.now}");
                Assert.That(second.head - second.presented, Is.LessThanOrEqualTo(8d), $"held back by the last session at {second.now}");
            }
        }
    }
}
