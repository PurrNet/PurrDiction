namespace PurrNet.Prediction
{
    public struct PredictedIdentityState : IPredictedData<PredictedIdentityState>
    {
        public PlayerID? owner;
        public bool wasOnSimulationStartCalled;

        public override string ToString()
        {
            return $"{{owner: {owner?.ToString() ?? "NULL"}, wasOnSimulationStartCalled: {wasOnSimulationStartCalled}}}";
        }

        public void Dispose() { }
    }
}
