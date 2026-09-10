using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionPerformanceTelemetryTests
    {
        private GameObject _worldObject;

        [TearDown]
        public void TearDown()
        {
            if (PredictionPerformanceTelemetry.isCollecting)
                PredictionPerformanceTelemetry.End();
            PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
            if (_worldObject)
                Object.DestroyImmediate(_worldObject);
        }

        [Test]
        public void CadencePreservesDefaultServerAndInitialSynchronization()
        {
            Assert.That(PredictionManager.ShouldDeferReconciliation(false, 10, 0, 1.001, 1), Is.False);
            Assert.That(PredictionManager.ShouldDeferReconciliation(true, 10, 0.05, 1.001, 1), Is.False);
            Assert.That(PredictionManager.ShouldDeferReconciliation(false, 0, 0.05, 1.001, 1), Is.False);
            Assert.That(PredictionManager.ShouldDeferReconciliation(false, 10, 0.05, 1.001, 1), Is.True);
            Assert.That(PredictionManager.ShouldDeferReconciliation(false, 10, 0.05, 1.06, 1), Is.False);
        }

        [Test]
        public void PassAccountingSeparatesVerifiedReplayAndRestoredPhysics()
        {
            _worldObject = new GameObject("PredictionPerformanceTelemetryTests");
            var world = _worldObject.AddComponent<PredictionManager>();
            PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0.05;
            PredictionPerformanceTelemetry.Begin(world);

            RunPass(world, PredictionPassKind.Forward);
            using (PredictionPerformanceTelemetry.BeginBatch(world, 1))
            {
                PredictionPerformanceTelemetry.StateRestored(world);
                RunPass(world, PredictionPassKind.Verified);
                RunPass(world, PredictionPassKind.SpeculativeReplay);
                RunPass(world, PredictionPassKind.SpeculativeReplay);
            }

            var result = PredictionPerformanceTelemetry.End();
            Assert.That(result.forward.count, Is.EqualTo(1));
            Assert.That(result.verified.count, Is.EqualTo(1));
            Assert.That(result.speculativeReplay.count, Is.EqualTo(2));
            Assert.That(result.gapCatchup.count, Is.Zero);
            Assert.That(result.firstPhysicsAfterRestore.count, Is.EqualTo(1));
            Assert.That(result.otherPhysics.count, Is.EqualTo(3));
            Assert.That(result.appliedVerifiedFrames, Is.EqualTo(1));
            Assert.That(result.correctionBatches, Is.EqualTo(1));
            Assert.That(result.replayDepthTicks.mean, Is.EqualTo(2));
            Assert.That(result.verifiedFramesPerBatch.mean, Is.EqualTo(1));
            Assert.That(PredictionPerformanceTelemetry.reconcileIntervalSeconds, Is.EqualTo(0.05),
                "Ending measurement must preserve the cadence being validated afterward.");
        }

        [Test]
        public void HistogramPreservesTotalsAndBoundsItsReportedPercentiles()
        {
            var metric = new PredictionPerformanceTelemetry.MetricAccumulator();
            for (int i = 1; i <= 100; i++)
                metric.Add(i);
            var result = metric.Snapshot();
            Assert.That(result.count, Is.EqualTo(100));
            Assert.That(result.total, Is.EqualTo(5050));
            Assert.That(result.mean, Is.EqualTo(50.5));
            Assert.That(result.min, Is.EqualTo(1));
            Assert.That(result.max, Is.EqualTo(100));
            Assert.That(result.p50, Is.InRange(50, 50 * 1.022));
            Assert.That(result.p95, Is.InRange(95, 95 * 1.022));
            Assert.That(result.p99, Is.InRange(99, 100));
        }

        private static void RunPass(PredictionManager world, PredictionPassKind kind)
        {
            using var pass = PredictionPerformanceTelemetry.BeginPass(world, kind);
            pass.PrepareDone(pass.Timestamp());
            pass.SimulateDone(pass.Timestamp());
            pass.PhysicsDone(pass.Timestamp());
            pass.LateDone(pass.Timestamp());
        }
    }
}
