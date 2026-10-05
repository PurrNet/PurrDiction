using NUnit.Framework;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class TickAgreementOracleTests
    {
        private GameObject _object;
        private TickAgreementShooter _shooter;
        private static readonly uint[] FinalTicks = { 1840, 1880, 1920 };

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("Tick agreement oracle");
            _shooter = _object.AddComponent<TickAgreementShooter>();
            var ticks = DisposableList<uint>.Create(3);
            foreach (var tick in FinalTicks)
                ticks.Add(tick);
            _shooter.currentState = new TickAgreementShooter.ShooterState { shots = 3, shotTicks = ticks };
        }

        [TearDown]
        public void TearDown()
        {
            _shooter.currentState.Dispose();
            _shooter.currentState = default;
            Object.DestroyImmediate(_object);
        }

        [Test]
        public void DiscardedSpeculativeAttemptDoesNotReplaceTheAcceptedRequest()
        {
            Attempt(1800, 1, 1800);
            CompleteAcceptedShots();
            // Reproduce the failing network trace: the old ordinal-based diagnostic
            // retains the abandoned first attempt even after authoritative correction.
            _shooter.firstPredictedTicks.AddRange(new uint[] { 1800, 1880, 1920 });

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.True, result.message);
            Assert.That(_shooter.TickDigest(), Does.Contain("requests=1840/1,1880/2,1920/3"));
        }

        [Test]
        public void TheSameRequestAcceptedAtADifferentTickStillFails()
        {
            Attempt(1800, 1, 1800);
            Authority(1840, 1800, 1, 0);
            CompleteAcceptedShots(1);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("tick disagreement").And.Contain("1800").And.Contain("1840"));
        }

        [Test]
        public void AuthoritativeRequestWithoutForwardAcceptanceFails()
        {
            _shooter.Observe("generated", 1840, Input(1840, 1), 0, 0, false);
            Authority(1840, 1840, 1, 0);
            CompleteAcceptedShots(1);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("missing forward acceptance").And.Contain("1840/1"));
        }

        [Test]
        public void EqualSimulationTicksCannotHideDifferentRequestKeys()
        {
            Attempt(1840, 2, 1840);
            Authority(1840, 1840, 1, 0);
            CompleteAcceptedShots(1);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("missing generated input").And.Contain("1840/1"));
        }

        [Test]
        public void SpeculativeOrdinalMayDifferFromFinalAuthoritativeShotIndex()
        {
            // A prior speculative shot can be rejected while a later attempt survives.
            // Its request ordinal describes the original attempt, not the final index.
            Attempt(1840, 2, 1840);
            Authority(1840, 1840, 2, 0);
            CompleteAcceptedShots(1);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.True, result.message);
            Assert.That(_shooter.TickDigest(), Does.Contain("requests=1840/2,1880/2,1920/3"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StateCorrectionWithoutAuthoritativeObservationFailsClosed(bool locallyOwned)
        {
            Attempt(1840, 1, 1840);
            CompleteAcceptedShots(1);

            var result = _shooter.ValidateTickAgreement(locallyOwned, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("missing authoritative acceptance").And.Contain("1840"));
        }

        [Test]
        public void ReplayingTheSameAuthoritativeRequestDoesNotCreateAmbiguity()
        {
            CompleteAcceptedShots();
            for (var replay = 0; replay < 5; replay++)
                Authority(1840, 1840, 1, 0);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.True, result.message);
        }

        [Test]
        public void ContradictoryAuthoritativeRequestsAtTheSameFinalTickFailClosed()
        {
            CompleteAcceptedShots();
            Authority(1840, 1839, 1, 0);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("ambiguous authoritative input requests"));
        }

        [Test]
        public void TruncatedObservationLogCannotPassFromItsRemainingSamples()
        {
            CompleteAcceptedShots();
            for (uint i = 0; i < 130; i++)
                _shooter.Observe("generated", 10000 + i, Input(10000 + i, 1), 0, 0, false);

            var result = _shooter.ValidateTickAgreement(true, false);

            Assert.That(result.success, Is.False);
            Assert.That(result.message, Does.Contain("observation log truncated"));
        }

        [Test]
        public void ObserverChecksAuthoritativeRequestsWithoutRequiringLocalInputGeneration()
        {
            for (var i = 0; i < FinalTicks.Length; i++)
                Authority(FinalTicks[i], FinalTicks[i], i + 1, i);

            var result = _shooter.ValidateTickAgreement(false, false);

            Assert.That(result.success, Is.True, result.message);
        }

        [Test]
        public void HostOwnedInputUsesItsForwardServerAcceptance()
        {
            for (var i = 0; i < FinalTicks.Length; i++)
            {
                var input = Input(FinalTicks[i], i + 1);
                _shooter.Observe("generated", FinalTicks[i], input, i, 0, false);
                _shooter.Observe("server", FinalTicks[i], input, i, 0, true);
            }

            var result = _shooter.ValidateTickAgreement(true, true);

            Assert.That(result.success, Is.True, result.message);
        }

        private void CompleteAcceptedShots(int first = 0)
        {
            for (var i = first; i < FinalTicks.Length; i++)
            {
                Attempt(FinalTicks[i], i + 1, FinalTicks[i]);
                Authority(FinalTicks[i], FinalTicks[i], i + 1, i);
            }
        }

        private void Attempt(uint requestTick, int ordinal, uint simulatedTick)
        {
            var input = Input(requestTick, ordinal);
            _shooter.Observe("generated", requestTick, input, ordinal - 1, 0, false);
            _shooter.Observe("forward", simulatedTick, input, ordinal - 1, 0, true);
        }

        private void Authority(uint simulatedTick, uint requestTick, int ordinal, int shotsBefore)
            => _shooter.Observe("verified-replay", simulatedTick, Input(requestTick, ordinal), shotsBefore, 0, true);

        private static TickAgreementShooter.ShotInput Input(uint tick, int ordinal)
            => new() { shoot = true, requestedTick = tick, requestedOrdinal = ordinal };
    }
}
