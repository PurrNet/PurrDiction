using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests
{
    public struct FallingSandState : IPredictedData<FallingSandState>, IDuplicate<FallingSandState>
    {
        public DisposableArray<bool> grid;
        public DisposableList<Size> dirtyIndexes;
        public PredictedRandom random;

        public void Dispose()
        {
            grid.Dispose();
        }

        public FallingSandState Duplicate()
        {
            return new FallingSandState
            {
                dirtyIndexes = DisposableList<Size>.Create(dirtyIndexes),
                grid = DisposableArray<bool>.Create(grid),
                random = random
            };
        }
    }
}
