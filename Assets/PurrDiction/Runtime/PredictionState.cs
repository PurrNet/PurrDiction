using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictionState : IPredictedData<PredictionState>
    {
        public PlayerID? owner;
        public PredictionTransformState? transform;
        
        public PredictionState Add(PredictionState a, PredictionState b)
        {
            a.transform = a.transform.HasValue && b.transform.HasValue
                ? new PredictionTransformState
                {
                    position = a.transform.Value.position + b.transform.Value.position,
                    rotation = a.transform.Value.rotation * b.transform.Value.rotation
                }
                : null;
            return a;
        }

        public PredictionState Negate(PredictionState a)
        {
            a.transform = a.transform.HasValue
                ? new PredictionTransformState
                {
                    position = -a.transform.Value.position,
                    rotation = Quaternion.Inverse(a.transform.Value.rotation)
                }
                : null;
            return a;
        }

        public PredictionState Scale(PredictionState a, float b)
        {
            a.transform = a.transform.HasValue
                ? new PredictionTransformState
                {
                    position = a.transform.Value.position * b,
                    rotation = Quaternion.Slerp(Quaternion.identity, a.transform.Value.rotation, b)
                }
                : null;
            
            return a;
        }

        public override string ToString()
        {
            return $"(owner: {owner}, transform: {transform.HasValue})";
        }
    }
}