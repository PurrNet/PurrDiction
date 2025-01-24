using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct CachedGameObject
    {
        public readonly float time;
        public readonly GameObject gameObject;
        
        public CachedGameObject(float time, GameObject gameObject)
        {
            this.time = time;
            this.gameObject = gameObject;
        }
    }

    public readonly struct PooledInstance
    {
        public readonly GameObject gameObject;
        public readonly Vector3 spawnPosition;
        public readonly Quaternion spawnRotation;
        
        public PooledInstance(GameObject gameObject, Vector3 spawnPosition, Quaternion spawnRotation)
        {
            this.gameObject = gameObject;
            this.spawnPosition = spawnPosition;
            this.spawnRotation = spawnRotation;
        }
    }
    
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly int prefabId;
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
    }
}