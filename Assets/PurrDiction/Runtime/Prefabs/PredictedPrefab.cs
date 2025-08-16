using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct PoolSettings
    {
        public bool usePooling;
        public int initialSize;
    }

    [Serializable]
    public struct PredictedPrefab
    {
        public GameObject prefab;
        public PoolSettings pooling;
    }
}
