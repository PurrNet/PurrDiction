using PurrNet.Packing;

namespace PurrNet.Prediction.Tests
{
    public struct SimpleWASDInput : IPredictedData
    {
        public NormalizedFloat horizontal;
        public NormalizedFloat vertical;
        public bool jump;
        public bool dash;

        public override string ToString()
        {
            return $"(horizontal: {horizontal}, vertical: {vertical}, jump: {jump}, dash: {dash})";
        }
    }

    public struct SimpleCCState : IPredictedData<SimpleCCState>
    {
        public float rotation;
        public bool wasShooting;
    }
}
