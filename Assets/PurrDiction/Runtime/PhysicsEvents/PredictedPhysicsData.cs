using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPhysicsData : IPredictedData<PredictedPhysicsData>, IDuplicate<PredictedPhysicsData>
    {
        public DisposableList<PhysicsEvent> events;

        public void Dispose()
        {
            if (events.isDisposed)
                return;

            int count = events.Count;
            for (var i = 0; i < count; i++)
                events[i].Dispose();
            events.Dispose();
        }

        public PredictedPhysicsData Duplicate()
        {
            return new PredictedPhysicsData
            {
                events = events.Duplicate()
            };
        }

        public override string ToString()
            => events.isDisposed ? "{events=<disposed>}" : $"{{events({events.Count})={events}}}";
    }
}