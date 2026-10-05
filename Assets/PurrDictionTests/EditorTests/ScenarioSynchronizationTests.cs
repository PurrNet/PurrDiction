using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ScenarioSynchronizationTests
    {
        [SetUp]
        public void SetUp() => ScenarioSynchronization.Reset();

        [TearDown]
        public void TearDown() => ScenarioSynchronization.Reset();

        [Test]
        public void CompletingHigherBarrierDoesNotReleaseLowerBarrier()
        {
            ScenarioBarrier.RecordResult(3, 22002, true, null);
            Assert.That(ScenarioBarrier.TryGetResult(3, 9210, out _), Is.False);

            ScenarioBarrier.RecordResult(3, 9210, true, null);
            Assert.That(ScenarioBarrier.TryGetResult(3, 9210, out var result), Is.True);
            Assert.That(result.success, Is.True);
        }

        [Test]
        public void FailedBarrierDeliveryRemainsFailureDespiteDuplicateSuccess()
        {
            // RecordResult is the receiver invoked by the production BroadcastResult RPC.
            ScenarioBarrier.RecordResult(2, 17, false, "arrived=1/2");
            ScenarioBarrier.RecordResult(2, 17, true, null);

            Assert.That(ScenarioBarrier.TryGetResult(2, 17, out var result), Is.True);
            Assert.That(result.success, Is.False);
            Assert.That(result.message, Is.EqualTo("arrived=1/2"));
            Assert.That(ScenarioBarrier.TryGetResult(3, 17, out _), Is.False);
        }

        [Test]
        public void LateOldDigestCannotContaminateEarlyNextScenarioReport()
        {
            var player = new PlayerID(7, false);
            DigestExchange.RecordReport(4, 1300, player, "old");
            DigestExchange.RecordReport(5, 1300, player, "new");

            ScenarioSynchronization.EndScenario(4);
            DigestExchange.RecordReport(4, 1300, player, "late old");
            ScenarioSynchronization.BeginScenario(5);

            Assert.That(DigestExchange.TryGetReports(4, 1300, out _), Is.False);
            Assert.That(DigestExchange.TryGetReports(5, 1300, out var reports), Is.True);
            Assert.That(reports.Count, Is.EqualTo(1));
            Assert.That(reports[player], Is.EqualTo("new"));

            // Exercise the real comparison with a complete report set, so no transport is needed.
            var ctx = new ScenarioContext { scenarioIndex = 5, role = NetworkRole.Server, expectedConnections = 1 };
            Assert.That(DigestExchange.Compare(ctx, 1300, "new", 1f).GetAwaiter().GetResult().success, Is.True);
        }

        [Test]
        public void ScopedComparisonStillFailsActualCurrentScenarioMismatch()
        {
            DigestExchange.RecordReport(1, 1500, new PlayerID(7, false), "different");
            var ctx = new ScenarioContext { scenarioIndex = 1, role = NetworkRole.Server, expectedConnections = 1 };

            var result = DigestExchange.Compare(ctx, 1500, "server", 1f).GetAwaiter().GetResult();

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("different").And.Contain("server"));
        }

        [Test]
        public void CompletedHostComparisonCanBeSharedBySecondRunSplitHalf()
        {
            var player = new PlayerID(7, false);
            DigestExchange.RecordReport(2, 31, player, "agreed");
            var ctx = new ScenarioContext { scenarioIndex = 2, role = NetworkRole.Host, expectedConnections = 2 };

            var first = DigestExchange.Compare(ctx, 31, "agreed", 1f).GetAwaiter().GetResult();
            DigestExchange.RecordReport(2, 31, player, "late duplicate");
            var second = DigestExchange.Compare(ctx, 31, "agreed", 1f).GetAwaiter().GetResult();

            Assert.That(first.success, Is.True);
            Assert.That(second.success, Is.True);
            Assert.That(second.message, Is.EqualTo(first.message));
        }

        [Test]
        public void StandaloneHostWithNoExternalClientsCompletesWithoutReports()
        {
            var ctx = new ScenarioContext { scenarioIndex = 0, role = NetworkRole.Host, expectedConnections = 1 };

            var result = DigestExchange.Compare(ctx, 400, "local digest", 1f).GetAwaiter().GetResult();

            Assert.That(result.success, Is.True);
            Assert.That(result.message, Is.EqualTo("local digest"));
        }

        [Test]
        public void EarlyBarrierAndDigestTickSurviveBeginningTheirScenario()
        {
            ScenarioBarrier.RecordResult(7, 12, true, null);
            DigestGate.RecordDigestTick(7, 100, 600);
            ScenarioSynchronization.EndScenario(6);
            ScenarioSynchronization.BeginScenario(7);

            Assert.That(ScenarioBarrier.TryGetResult(7, 12, out var result), Is.True);
            Assert.That(result.success, Is.True);
            Assert.That(DigestGate.TryGetDigestTick(7, 100, out var tick), Is.True);
            Assert.That(tick, Is.EqualTo(600ul));
        }

        [Test]
        public void ClosingEpochRejectsLateBarrierAndTickAndCannotBeReopened()
        {
            ScenarioSynchronization.EndScenario(3);
            ScenarioBarrier.RecordResult(3, 12, true, null);
            DigestGate.RecordDigestTick(3, 100, 600);

            Assert.That(ScenarioBarrier.TryGetResult(3, 12, out _), Is.False);
            Assert.That(DigestGate.TryGetDigestTick(3, 100, out _), Is.False);
            Assert.Throws<InvalidOperationException>(() => ScenarioSynchronization.BeginScenario(3));
        }

        [Test]
        public void ReconnectWithinOpenEpochPreservesProgressAndResetStartsFreshRun()
        {
            ScenarioSynchronization.BeginScenario(4);
            ScenarioBarrier.RecordResult(4, 12, true, null);
            DigestGate.RecordDigestTick(4, 100, 600);
            ScenarioSynchronization.BeginScenario(4);
            Assert.That(ScenarioBarrier.TryGetResult(4, 12, out _), Is.True);

            ScenarioSynchronization.EndScenario(4);
            ScenarioSynchronization.Reset();
            Assert.DoesNotThrow(() => ScenarioSynchronization.BeginScenario(0));
            Assert.That(ScenarioBarrier.TryGetResult(4, 12, out _), Is.False);
            Assert.That(DigestGate.TryGetDigestTick(4, 100, out _), Is.False);
        }
    }
}
