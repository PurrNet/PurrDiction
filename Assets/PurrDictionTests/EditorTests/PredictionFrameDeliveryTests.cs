using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionFrameDeliveryTests
    {
        [Test]
        public void InputRedundancyTracksTheInputMarginHighBand()
        {
            Assert.That(PredictionManager.InputRedundancyTicks(20), Is.EqualTo(5));
            Assert.That(PredictionManager.InputRedundancyTicks(64), Is.EqualTo(7));
            Assert.That(PredictionManager.InputRedundancyTicks(128), Is.EqualTo(13));
            Assert.That(PredictionManager.InputRedundancyTicks(400), Is.EqualTo(32));

            Assert.That(
                PredictionManager.InputRedundancyTicks(100),
                Is.EqualTo((ulong)(2 * PredictionManager.InputMarginTargetTicks(100) + 1)));
        }
    }
}
