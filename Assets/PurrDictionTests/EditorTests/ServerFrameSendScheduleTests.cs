using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ServerFrameSendScheduleTests
    {
        [TestCase(0, 60)]
        [TestCase(1, 1)]
        [TestCase(20, 20)]
        [TestCase(24, 24)]
        [TestCase(25, 25)]
        [TestCase(30, 30)]
        [TestCase(40, 40)]
        [TestCase(59, 59)]
        [TestCase(60, 60)]
        [TestCase(120, 60)]
        public void TargetRatePreservesFractionalIntervalsWithoutDrift(int requested, int expected)
        {
            var schedule = new ServerFrameSendSchedule();
            schedule.MarkSent(101, true);
            int sent = 0;
            for (ulong tick = 102; tick <= 701; tick++)
            {
                if (!schedule.ShouldSend(tick, 60, requested))
                    continue;
                schedule.MarkSent(tick, false);
                sent++;
                Assert.That(schedule.ShouldSend(tick, 60, requested), Is.False);
            }
            Assert.That(sent, Is.EqualTo(expected * 10));
        }

        [Test]
        public void CatchUpDoesNotBankMissedSendOpportunities()
        {
            var schedule = new ServerFrameSendSchedule();
            schedule.MarkSent(10, true);
            Assert.That(schedule.ShouldSend(1000, 60, 30), Is.True);
            schedule.MarkSent(1000, false);
            Assert.That(schedule.ShouldSend(1000, 60, 30), Is.False);
            Assert.That(schedule.ShouldSend(1001, 60, 30), Is.False);
            Assert.That(schedule.ShouldSend(1002, 60, 30), Is.True);
        }

        [Test]
        public void FullCheckpointReanchorsOrdinarySendCadence()
        {
            var schedule = new ServerFrameSendSchedule();
            schedule.MarkSent(10, true);
            schedule.MarkSent(15, true);
            Assert.That(schedule.ShouldSend(16, 60, 20), Is.False);
            Assert.That(schedule.ShouldSend(17, 60, 20), Is.False);
            Assert.That(schedule.ShouldSend(18, 60, 20), Is.True);
        }

        [Test]
        public void RuntimeRateChangeTakesEffectFromTheNextSlot()
        {
            var schedule = new ServerFrameSendSchedule();
            schedule.MarkSent(10, true);
            Assert.That(schedule.ShouldSend(11, 60, 30), Is.False);
            Assert.That(schedule.ShouldSend(12, 60, 30), Is.True);
            schedule.MarkSent(12, false);
            Assert.That(schedule.ShouldSend(13, 60, 0), Is.True);
            schedule.MarkSent(13, false);
            Assert.That(schedule.ShouldSend(15, 60, 20), Is.False);
            Assert.That(schedule.ShouldSend(16, 60, 20), Is.True);
        }

        [Test]
        public void ObserverDisposeClearsCadence()
        {
            var frame = new PlayerPacker { preparedFrameTick = 17 };
            frame.frameSendSchedule.MarkSent(17, true);
            frame.Dispose();
            Assert.That(frame.frameSendSchedule.lastSentTick, Is.Zero);
            Assert.That(frame.frameSendSchedule.ShouldSend(1, 60, 1), Is.True);
        }

        [Test]
        public void TickZeroAndOldFramesNeverBecomeDue()
        {
            var schedule = new ServerFrameSendSchedule();
            Assert.That(schedule.ShouldSend(0, 60, 0), Is.False);
            schedule.MarkSent(100, true);
            Assert.That(schedule.ShouldSend(99, 60, 0), Is.False);
            Assert.That(schedule.ShouldSend(100, 60, 0), Is.False);
        }

        [Test]
        public void LargeTickValuesDoNotOverflowPhaseArithmetic()
        {
            var schedule = new ServerFrameSendSchedule();
            schedule.MarkSent(1, true);
            Assert.That(schedule.ShouldSend(ulong.MaxValue - 1, 60, 40), Is.True);
            schedule.MarkSent(ulong.MaxValue - 1, false);
            Assert.DoesNotThrow(() => schedule.ShouldSend(ulong.MaxValue, 60, 40));
        }

        [Test]
        public void UpdateRateDefaultsToSimulationAndRejectsNegativeValues()
        {
            var go = new GameObject("Server rate test");
            try
            {
                var manager = go.AddComponent<PredictionManager>();
                Assert.That(manager.serverUpdateRate, Is.Zero);
                manager.serverUpdateRate = 30;
                Assert.That(manager.serverUpdateRate, Is.EqualTo(30));
                Assert.Throws<ArgumentOutOfRangeException>(() => manager.serverUpdateRate = -1);
                Assert.That(manager.serverUpdateRate, Is.EqualTo(30));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
