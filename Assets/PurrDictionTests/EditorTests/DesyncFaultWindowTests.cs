using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DesyncFaultWindowTests
    {
        [Test]
        public void AuthoritativeReplacementAfterTriggerStillReceivesAnArmedFault()
        {
            var state = new DesyncProbe.ProbeState { count = 99 };
            Assert.That(DesyncProbe.AdvanceWithFault(ref state, 100, 100), Is.True);
            Assert.That(state.count, Is.EqualTo(10099UL));

            // A full authoritative replacement can skip the original count crossing.
            // The fault window must remain armed until a native notification arrives.
            state = new DesyncProbe.ProbeState { count = 140 };
            Assert.That(DesyncProbe.AdvanceWithFault(ref state, 141, 100), Is.True);
            Assert.That(state.count, Is.EqualTo(10140UL));
        }

        [Test]
        public void RollbackBeforeFaultStartDoesNotCorruptEarlierHistory()
        {
            var state = new DesyncProbe.ProbeState { count = 40 };
            Assert.That(DesyncProbe.AdvanceWithFault(ref state, 41, 100), Is.False);
            Assert.That(state.count, Is.EqualTo(41UL));
        }

        [Test]
        public void StoppingFaultLeavesExistingErrorForNativeCorrectionToRepair()
        {
            var state = new DesyncProbe.ProbeState { count = 10099 };
            Assert.That(DesyncProbe.AdvanceWithFault(ref state, 101, 0), Is.False);
            Assert.That(state.count, Is.EqualTo(10100UL), "Stopping injection must not heal the state itself.");

            state = new DesyncProbe.ProbeState { count = 150 };
            Assert.That(DesyncProbe.AdvanceWithFault(ref state, 151, 0), Is.False);
            Assert.That(state.count, Is.EqualTo(151UL));
        }
    }
}
