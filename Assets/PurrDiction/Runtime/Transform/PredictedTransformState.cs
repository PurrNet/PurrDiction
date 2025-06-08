using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictedTransformState : IPredictedData<PredictedTransformState>
    {
        public Vector3 unityPosition;
        public Quaternion unityRotation;

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            unityPosition = position;
            unityRotation = rotation;
        }

        public void SetPositionAndRotation(Transform trs)
        {
            trs.GetPositionAndRotation(out unityPosition, out unityRotation);
        }

        public override string ToString()
        {
            return $"P: {unityPosition}\nR: {unityRotation}";
        }

        public void Dispose() { }
    }
}
