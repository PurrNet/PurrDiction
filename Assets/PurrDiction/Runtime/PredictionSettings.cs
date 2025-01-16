using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public class PredictionSettings
    {
        [Tooltip("Maximum number of inputs to buffer before dropping old ones.")]
        public int maxInputBufferCount = 4;
        
        [Tooltip("The number of seconds to keep in the history for rollback purposes and redundancy.\n" +
                 "Naturally, this means more memory usage.")]
        public int secondsToKeepInHistory = 5;
        
        public bool interpolate = true;
    }
}