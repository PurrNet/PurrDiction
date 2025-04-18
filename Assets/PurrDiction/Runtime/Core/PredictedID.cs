using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct PredictedID : IPackedAuto, IEquatable<PredictedID>
    {
        public readonly PackedUInt value;

        public PredictedIdentity GetIdentity(PredictionManager manager)
        {
            return manager.GetIdentity(this);
        }

        public T GetIdentity<T>(PredictionManager manager) where T : PredictedIdentity
        {
            return (T)manager.GetIdentity(this);
        }

        public bool TryGetIdentity(PredictionManager manager, out PredictedIdentity identity)
        {
            identity = manager.GetIdentity(this);
            return identity != null;
        }

        public bool TryGetIdentity<T>(PredictionManager manager, out T identity) where T : PredictedIdentity
        {
            identity = (T)manager.GetIdentity(this);
            return identity != null;
        }

        public PredictedID(uint id)
        {
            this.value = id;
        }

        public bool Equals(PredictedID other)
        {
            return value == other.value;
        }

        public override bool Equals(object obj)
        {
            return obj is PredictedID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)value.value;
        }
    }
}