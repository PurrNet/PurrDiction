namespace PurrNet.Prediction
{
    public struct PredictedIdentityState : IPredictedData<PredictedIdentityState>
    {
        public PlayerID? owner;
        public PredictedID predictedID;
    }
}