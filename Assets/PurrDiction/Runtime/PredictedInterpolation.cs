using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct PredictedInterpolation
    {
        [Tooltip("The rate at which the object interpolates towards the target.")]
        public Vector2 correctionRateMinMax;
        [Tooltip("This controls the correction rate based on the distance between the current and target.")]
        public Vector2 correctionBlendMinMax;
        [Tooltip("The minimum and maximum distance at which the object will teleport to the target.")]
        public Vector2 teleportThresholdMinMax;
    }
}