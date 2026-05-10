using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public struct PhysicsEvent : IPackedAuto, IDisposable, IDuplicate<PhysicsEvent>
    {
        public bool isTrigger;
        public PhysicsEventType type;
        public PredictedComponentID me;
        public PredictedComponentID other;
        public PhysicsCollision collision;

        public void Dispose() => collision.Dispose();

        public PhysicsEvent Duplicate()
        {
            return new PhysicsEvent
            {
                isTrigger = isTrigger,
                type = type,
                me = me,
                other = other,
                collision = collision.Duplicate()
            };
        }

        public override string ToString()
            => $"{{type={type}, isTrigger={isTrigger}, me={me}, other={other}, collision={collision}}}";
    }
}