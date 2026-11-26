using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictedIdEqualityOperatorTests
    {
        [Test]
        public void ObjectIdOperatorsMatchEquals()
        {
            var a = new PredictedObjectID(3);
            var same = new PredictedObjectID(3);
            var other = new PredictedObjectID(4);

            Assert.IsTrue(a == same);
            Assert.IsFalse(a != same);
            Assert.IsFalse(a == other);
            Assert.IsTrue(a != other);
            Assert.IsTrue(default(PredictedObjectID) == new PredictedObjectID(0));
        }

        [Test]
        public void NullableObjectIdOperators()
        {
            PredictedObjectID? a = new PredictedObjectID(3);
            PredictedObjectID? same = new PredictedObjectID(3);
            PredictedObjectID? other = new PredictedObjectID(4);
            PredictedObjectID? none = null;

            Assert.IsTrue(a == same);
            Assert.IsTrue(a != other);
            Assert.IsTrue(a != none);
            Assert.IsTrue(none == null);
            Assert.IsFalse(a == null);
        }

        [Test]
        public void ComponentIdOperatorsMatchEquals()
        {
            var a = new PredictedComponentID(new PredictedObjectID(3), 1);
            var same = new PredictedComponentID(new PredictedObjectID(3), 1);
            var otherComponent = new PredictedComponentID(new PredictedObjectID(3), 2);
            var otherObject = new PredictedComponentID(new PredictedObjectID(4), 1);

            Assert.IsTrue(a == same);
            Assert.IsFalse(a != same);
            Assert.IsFalse(a == otherComponent);
            Assert.IsTrue(a != otherComponent);
            Assert.IsFalse(a == otherObject);
            Assert.IsTrue(a != otherObject);
        }

        [Test]
        public void NullableComponentIdOperators()
        {
            // The reported case: state.slot1Weapon != state.currentWeapon on PredictedComponentID? fields.
            PredictedComponentID? slot1Weapon = new PredictedComponentID(new PredictedObjectID(3), 1);
            PredictedComponentID? currentWeapon = new PredictedComponentID(new PredictedObjectID(3), 1);
            PredictedComponentID? otherWeapon = new PredictedComponentID(new PredictedObjectID(5), 1);
            PredictedComponentID? none = null;

            Assert.IsFalse(slot1Weapon != currentWeapon);
            Assert.IsTrue(slot1Weapon == currentWeapon);
            Assert.IsTrue(slot1Weapon != otherWeapon);
            Assert.IsTrue(slot1Weapon != none);
            Assert.IsTrue(none == null);
            Assert.IsFalse(slot1Weapon == null);
        }
    }
}
