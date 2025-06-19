using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct PredictedObjectID : IPackedAuto, IEquatable<PredictedObjectID>
    {
        public readonly PackedUInt instanceId;
        
        public PredictedObjectID(uint instanceId)
        {
            this.instanceId = instanceId;
        }

        public bool Equals(PredictedObjectID other)
        {
            return instanceId == other.instanceId;
        }

        public override bool Equals(object obj)
        {
            return obj is PredictedObjectID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)instanceId.value;
        }
    }
}
