using PurrNet.Prediction;

/// <summary>A stable ordinary replicated state whose omitted records require a real baseline.</summary>
public sealed class BaselineRecoveryProbe : PredictedIdentity<BaselineRecoveryProbe.State>
{
    public struct State : IPredictedData<State>
    {
        public int sentinel;
        public void Dispose() { }
    }

    public const int ExpectedSentinel = 731923;
    protected override State GetInitialState() => new State { sentinel = ExpectedSentinel };
}
