using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests
{
    public struct FallingSandState : IPredictedData<FallingSandState>
    {
        public DisposableArray<bool> grid;
        public DisposableList<Size> dirtyIndexes;
        public PredictedRandom random;

        public void Dispose()
        {
            grid.Dispose();
        }
    }
}
