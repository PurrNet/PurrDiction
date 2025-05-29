using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly PackedInt prefabId;
        public readonly PredictedObjectID instanceId;
        public readonly Vector3 spawnPosition;
        public readonly Quaternion spawnRotation;

        public InstanceDetails(int prefabId, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation)
        {
            this.prefabId = prefabId;
            this.instanceId = instanceId;
            this.spawnPosition = spawnPosition;
            this.spawnRotation = spawnRotation;
        }

        public bool Equals(InstanceDetails other)
        {
            return prefabId == other.prefabId && instanceId.Equals(other.instanceId) &&
                   spawnPosition.Equals(other.spawnPosition) &&
                   spawnRotation.Equals(other.spawnRotation);
        }

        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, instanceId, spawnPosition, spawnRotation);
        }

        public override string ToString()
        {
            return $"PrefabId: {prefabId}\nInstanceId: {instanceId}\nSpawnPosition: {spawnPosition}\nSpawnRotation: {spawnRotation}";
        }
    }
}
