using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct PredictionSettings
    {
        public int maxInterpolationQueue;
        
        [Tooltip("The number of seconds to keep in the history for rollback purposes and redundancy.\n" +
                 "Naturally, this means more memory usage.")]
        public int secondsToKeepInHistory;
        public bool interpolate;

        [Tooltip("If auto-included, these will be used to interpolate the object's position.")]
        public PredictedInterpolation positionInterpolation;
        [Tooltip("If auto-included, these will be used to interpolate the object's rotation.")]
        public PredictedInterpolation rotationInterpolation;
        
        [Tooltip("Should the object's position and rotation be auto-included in the prediction data?")]
        public bool autoIncludeTransform;
    }
}