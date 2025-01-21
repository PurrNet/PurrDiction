using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictionTransformState : IPredictedData<PredictionTransformState>
    {
        public Vector3 position;
        public Quaternion rotation;
        
        public override string ToString()
        {
            return $"(position: {position}, rotation: {rotation})";
        }
    }
}