using UnityEngine;

namespace PurrNet.Prediction
{
    [CreateAssetMenu(fileName = "InterpolationSettings", menuName = "PurrNet/Purrdiction/InterpolationSettings", order = -402)]
    public class TransformInterpolationSettings : ScriptableObject
    {
        public bool useInterpolation = true;

        [Tooltip("If smoothing corrections, these will be used to interpolate the object's position.")]
        public PredictedInterpolation positionInterpolation = new PredictedInterpolation
        {
            correctionRateMinMax = new Vector2(3.3f, 10f),
            correctionBlendMinMax = new Vector2(0.25f, 4f),
            teleportThresholdMinMax = new Vector2(0.025f, 5f)
        };

        [Tooltip("If smoothing corrections, these will be used to interpolate the object's rotation.")]
        public PredictedInterpolation rotationInterpolation = new PredictedInterpolation
        {
            correctionRateMinMax = new Vector2(3.3f, 10f),
            correctionBlendMinMax = new Vector2(5f, 30f),
            teleportThresholdMinMax = new Vector2(1.5f, 52f)
        };
    }
}
