using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public struct PredictedTimeState : IPredictedData<PredictedTimeState>
    {
        public ulong tick;
        public NormalizedFloat timeScale;

        public override string ToString()
        {
            return $"tick={tick}\ntimeScale={timeScale}";
        }

        public void Dispose() { }
    }
}
