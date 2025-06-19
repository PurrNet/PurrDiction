namespace PurrNet.Prediction
{
    public struct PredictedIdentityState : IPredictedData<PredictedIdentityState>
    {
        public PlayerID? owner;
        // public PredictedID predictedID;

        public override string ToString()
        {
            return $"{{owner: {(owner?.ToString() ?? "NULL")}}}";
        }

        public void Dispose() { }
    }
}
