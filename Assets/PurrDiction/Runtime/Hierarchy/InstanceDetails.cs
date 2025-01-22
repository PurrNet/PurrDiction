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
    
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly int prefabId;
        public readonly PredictedObjectID instanceId;
        
        public InstanceDetails(int prefabId, PredictedObjectID instanceId)
        {
            this.prefabId = prefabId;
            this.instanceId = instanceId;
        }

        public bool Equals(InstanceDetails other)
        {
            return prefabId == other.prefabId && instanceId.Equals(other.instanceId);
        }
        
        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, instanceId);
        }
    }
}