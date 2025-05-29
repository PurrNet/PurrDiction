using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PooledInstance
    {
        public readonly GameObject gameObject;
        public readonly Vector3 spawnPosition;
        public readonly ulong addedTick;
        public readonly PredictedObjectID id;

        public PooledInstance(GameObject gameObject, Vector3 spawnPosition, ulong addedTick, PredictedObjectID id)
        {
            this.gameObject = gameObject;
            this.spawnPosition = spawnPosition;
            this.addedTick = addedTick;
            this.id = id;
        }
    }
}
