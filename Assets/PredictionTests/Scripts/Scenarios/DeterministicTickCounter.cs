using PurrNet.Prediction;

public class DeterministicTickCounter : DeterministicIdentity<DeterministicTickCounter.CounterState>
{
    public struct CounterState : IPredictedData<CounterState>
    {
        public ulong count;

        public void Dispose() { }
    }

    protected override void Simulate(ref CounterState state, sfloat delta)
    {
        state.count += 1;
    }
}
