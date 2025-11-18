using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests
{
    public struct ByteColor
    {
        public byte R, G, B;
    }

    public struct SandTile
    {
        public ByteColor? color;
        public bool hasValue => color.HasValue;
    }

    public struct FallingSandState : IPredictedData<FallingSandState>, IDuplicate<FallingSandState>
    {
        public DisposableArray<SandTile> grid;
        public DisposableList<Size> dirtyIndexes;
        public PredictedRandom random;

        public void Dispose()
        {
            grid.Dispose();
            dirtyIndexes.Dispose();
        }

        public FallingSandState Duplicate()
        {
            return new FallingSandState
            {
                dirtyIndexes = DisposableList<Size>.Create(dirtyIndexes),
                grid = DisposableArray<SandTile>.Create(grid),
                random = random
            };
        }
    }
}
