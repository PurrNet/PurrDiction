using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;

        public void Dispose()
        {
            packer?.Dispose();
        }
    }
}