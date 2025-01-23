using System;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct PredictionSettings
    {
        [Tooltip("Should the object's position and rotation be auto-included in the prediction data?"), PurrLock]
        public bool autoIncludeTransform;
        public bool interpolate;

        [Tooltip("If auto-included, these will be used to interpolate the object's position.")]
        public PredictedInterpolation positionInterpolation;
        [Tooltip("If auto-included, these will be used to interpolate the object's rotation.")]
        public PredictedInterpolation rotationInterpolation;
        public int maxInterpolationQueue;

    }
}