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

        /// <summary>
        /// Lowest numeric interest tier allowed by distance resolution for this prefab.
        /// Higher values mean lower detail: resolved tiers below this floor are raised to it,
        /// while 255 always starts culled. Providers, pins, and ownership may still override it.
        /// </summary>
        [Tooltip("Distance-based interest tier floor. 0 allows full detail; higher values force lower detail; 255 starts culled. Providers, pins, and ownership may override it.")]
        public byte minimumInterestTier;
    }
}
