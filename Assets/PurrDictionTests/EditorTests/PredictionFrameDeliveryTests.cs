using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionFrameDeliveryTests
    {
        [Test]
        public void ReliableFrameBlocksUntilItsTickIsAcknowledged()
        {
            ReliableFrameDeliveryState delivery = default;

            Assert.That(delivery.ShouldSuppress(0), Is.False);

            delivery.MarkSent(42);

            Assert.That(delivery.ShouldSuppress(0), Is.True);
            Assert.That(delivery.ShouldSuppress(41), Is.True);
            Assert.That(delivery.ShouldSuppress(42), Is.False);
            Assert.That(delivery.ShouldSuppress(0), Is.False);
        }

        [Test]
        public void ClearingReliableFrameStartsANewAcknowledgementEpoch()
        {
            ReliableFrameDeliveryState delivery = default;
            delivery.MarkSent(42);

            delivery.Clear();

            Assert.That(delivery.ShouldSuppress(0), Is.False);
        }

        [Test]
        public void FrameDeliveryUsesReliableOnlyBeyondTheFragmentableBudget()
        {
            int maxUnreliableFrameBytes = PredictionManager.GetMaxUnreliableFrameBytes(1023);

            Assert.That(maxUnreliableFrameBytes, Is.EqualTo(256191));
            Assert.That(PredictionManager.RequiresReliableRecovery(
                false,
                maxUnreliableFrameBytes,
                maxUnreliableFrameBytes), Is.False);
            Assert.That(PredictionManager.RequiresReliableRecovery(
                false,
                maxUnreliableFrameBytes + 1,
                maxUnreliableFrameBytes), Is.True);
            Assert.That(PredictionManager.RequiresReliableRecovery(
                true,
                0,
                maxUnreliableFrameBytes), Is.True);
        }
    }
}
