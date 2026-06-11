using PurrNet.Prediction;

public class PredictedMarker : PredictedIdentity<PredictedMarker.MarkerState>
{
    public struct MarkerState : IPredictedData<MarkerState>
    {
        public uint ticksAlive;

        public void Dispose() { }
    }

    protected override void Simulate(ref MarkerState state, float delta)
    {
        state.ticksAlive += 1;
    }
}
