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
            return $"horizontal: {horizontal}\nvertical: {vertical}\njump: {jump}\ndash: {dash})";
        }
    }

    public struct SimpleCCState : IPredictedData<SimpleCCState>
    {
        public float rotation;
        public bool wasShooting;

        public override string ToString()
        {
            return $"rotation: {rotation}\nwasShooting: {wasShooting}";
        }
    }
}
