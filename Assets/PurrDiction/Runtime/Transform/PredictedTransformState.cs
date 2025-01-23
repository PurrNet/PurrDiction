using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictedTransformState : IPredictedData<PredictedTransformState>
    {
        public Vector3 position;
        public Quaternion rotation;
    }
}