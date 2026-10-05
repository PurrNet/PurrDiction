using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class GenericSoftCorrectionProgressTests
    {
        [Test]
        public void UnrepairedFaultStillFailsAfterSixtyLiveIncrements()
        {
            var progress = new GenericSoftCorrectionProgress();
            progress.RecordTarget(200f);
            for (int i = 0; i < 60; i++) progress.AdvanceLiveTick();
            Assert.That(progress.Residual(200f + 100f + 60f), Is.EqualTo(100f));
            Assert.That(progress.Residual(360f), Is.GreaterThan(30f));
        }

        [Test]
        public void CorrectedStateWithFortyFiveLiveIncrementsHasNoResidualError()
        {
            var progress = new GenericSoftCorrectionProgress();
            progress.RecordTarget(200f);
            for (int i = 0; i < 45; i++) progress.AdvanceLiveTick();
            Assert.That(245f - progress.targetValue, Is.GreaterThan(30f));
            Assert.That(progress.Residual(245f), Is.Zero);

            progress.RecordTarget(230f); // A native full replacement resets the baseline too.
            progress.AdvanceLiveTick();
            Assert.That(progress.Residual(231f), Is.Zero);
        }
    }
}
