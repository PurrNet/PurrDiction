using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Obsolete("Use PredictedPrefab instead.")]
    [Serializable]
    public struct PoolSettings
    {
        public bool usePooling;
        public int initialSize;
    }

    [Serializable]
    public struct PredictedPrefab
    {
        public string guid;
        public GameObject prefab;
        public bool pooled;
        public int warmupCount;
    }
}
