using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PooledInstance
    {
        public readonly GameObject gameObject;
        public readonly Vector3 spawnPosition;
        public readonly ulong addedTick;

        public PooledInstance(GameObject gameObject, Vector3 spawnPosition, ulong addedTick)
        {
            this.gameObject = gameObject;
            this.spawnPosition = spawnPosition;
            this.addedTick = addedTick;
        }
    }
}