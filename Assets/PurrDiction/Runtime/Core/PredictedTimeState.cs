namespace PurrNet.Prediction
{
    public struct PredictedTimeState : IPredictedData<PredictedTimeState>
    {
        public ulong tick;

        public override string ToString()
        {
            return $"tick={tick}";
        }

        public void Dispose() { }
    }
}
