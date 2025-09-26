using System;
using System.Collections.Generic;
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
        public readonly PlayerID? owner;

        public InstanceDetails(int prefabId, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner)
        {
            this.prefabId = prefabId;
            this.instanceId = instanceId;
            this.spawnPosition = spawnPosition;
            this.spawnRotation = spawnRotation;
            this.owner = owner;
        }

        static bool Vector3CloseEnough(Vector3 a, Vector3 b)
        {
            return Vector3.Distance(a, b) < 0.0001f;
        }

        static bool QuaternionCloseEnough(Quaternion a, Quaternion b)
        {
            return Quaternion.Angle(a, b) < 0.0001f;
        }

        public bool Equals(InstanceDetails other)
        {
            return prefabId == other.prefabId && instanceId.Equals(other.instanceId) &&
                   spawnPosition.Equals(other.spawnPosition) &&
                   spawnRotation.Equals(other.spawnRotation) && owner == other.owner;
        }

        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, instanceId, spawnPosition, spawnRotation, owner);
        }

        public override string ToString()
        {
            return $"id: {instanceId}, {spawnPosition} | {spawnRotation}\n";
        }
    }
}
