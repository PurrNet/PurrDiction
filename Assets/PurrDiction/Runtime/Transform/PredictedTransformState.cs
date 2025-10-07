using PurrNet.Packing;
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

    public struct PredictedTransformCompressedState : IPredictedData<PredictedTransformCompressedState>
    {
        public CompressedVector3 unityPosition;
        public PackedQuaternion unityRotation;

        public PredictedTransformCompressedState(PredictedTransformState state)
        {
            unityPosition = new CompressedVector3(
                new CompressedFloat(state.unityPosition.x),
                new CompressedFloat(state.unityPosition.y),
                new CompressedFloat(state.unityPosition.z)
            );
            unityRotation = new PackedQuaternion(state.unityRotation);
        }

        public void Dispose() { }
    }

    public struct PredictedTransformHalfState : IPredictedData<PredictedTransformHalfState>
    {
        public HalfVector3 unityPosition;
        public HalfQuaternion unityRotation;

        public PredictedTransformHalfState(PredictedTransformState state)
        {
            unityPosition = state.unityPosition;
            unityRotation = state.unityRotation;
        }

        public void Dispose() { }
    }
}
