using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PredictedObjectID : IPackedAuto, IEquatable<PredictedObjectID>
    {
        public readonly PackedUInt instanceId;

        public PredictedObjectID(uint instanceId)
        {
            this.instanceId = instanceId;
        }

        public GameObject GetGameObject(PredictionManager manager)
        {
            return manager.hierarchy.GetGameObject(this);
        }

        public bool TryGetGameObject(PredictionManager manager, out GameObject gameObject)
        {
            return manager.hierarchy.TryGetGameObject(this, out gameObject);
        }

        public bool TryGetComponent<T>(PredictionManager manager, out T component)
        {
            return manager.hierarchy.TryGetComponent(this, out component);
        }

        public T GetComponent<T>(PredictionManager manager)
        {
            if (manager.hierarchy.TryGetComponent<T>(this, out var result))
                return result;
            return default;
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

        public override string ToString()
        {
            return $"{instanceId.value}";
        }
    }
}
