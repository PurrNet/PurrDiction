using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PredictedComponentID : IPackedAuto, IEquatable<PredictedComponentID>
    {
        public readonly PredictedObjectID objectId;
        public readonly PackedUInt componentId;

        public PredictedIdentity GetIdentity(PredictionManager manager)
        {
            return manager.GetIdentity(this);
        }

        public bool TryGetGameObject(PredictionManager manager, out GameObject gameObject)
        {
            gameObject = GetGameObject(manager);
            return gameObject;
        }

        public GameObject GetGameObject(PredictionManager manager)
        {
            var id = manager.GetIdentity(this);
            if (!id) return null;
            return id.gameObject;
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

        public bool TryGetIdentity<T>(PredictionManager manager, out T identity)
        {
            var id = manager.GetIdentity(this);

            if (!id || id is not T predictedIdentity)
            {
                identity = default;
                return false;
            }

            identity = predictedIdentity;
            return true;
        }

        public PredictedComponentID(PredictedObjectID objId, uint id)
        {
            objectId = objId;
            componentId = id;
        }

        public bool Equals(PredictedComponentID other)
        {
            return objectId.Equals(other.objectId) && componentId.value == other.componentId.value;
        }

        public override bool Equals(object obj)
        {
            return obj is PredictedComponentID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)(objectId.instanceId.value ^ componentId.value);
        }

        public override string ToString()
        {
            return $"PredictedID({objectId.instanceId.value}, {componentId.value})";
        }
    }
}
