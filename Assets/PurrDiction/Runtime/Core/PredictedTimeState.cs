namespace PurrNet.Prediction
{
    public struct PredictedTimeState : IPredictedData<PredictedTimeState>
    {
        public ulong tick;
        public float timeScale;

        public override string ToString()
        {
            return $"tick={tick}\ntimeScale={timeScale}";
        }

        public void Dispose() { }
    }
}
