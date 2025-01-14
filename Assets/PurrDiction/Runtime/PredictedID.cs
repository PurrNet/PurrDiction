using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct PredictedID : IPackedAuto
    {
        public readonly PackedInt id;
        
        public PredictedID(int id)
        {
            this.id = new PackedInt(id);
        }
    }
}