using BEPUutilities;
using ConversionHelper;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictedTransformState : IPredictedData<PredictedTransformState>
    {
        public bool isFP;

        public FPVector3 fpPosition;
        public FPQuaternion fpRotation;

        public Vector3 unityPosition;
        public Quaternion unityRotation;

        public Vector3 GetUnityPosition()
        {
            return isFP ? fpPosition.ToVector3() : unityPosition;
        }

        public Quaternion GetUnityRotation()
        {
            return isFP ? fpRotation.ToQuaternion() : unityRotation;
        }
    }

    public static class PredictedTransformStateSerializer
    {
        [UsedByIL]
        public static void Serialize(BitPacker packer, PredictedTransformState state)
        {
            Packer<bool>.Write(packer, state.isFP);

            if (state.isFP)
            {
                Packer<FPVector3>.Write(packer, state.fpPosition);
                Packer<FPQuaternion>.Write(packer, state.fpRotation);
            }
            else
            {
                Packer<Vector3>.Write(packer, state.unityPosition);
                Packer<Quaternion>.Write(packer, state.unityRotation);
            }
        }

        [UsedByIL]
        public static void Deserialize(BitPacker packer, ref PredictedTransformState state)
        {
            Packer<bool>.Read(packer, ref state.isFP);

            if (state.isFP)
            {
                Packer<FPVector3>.Read(packer, ref state.fpPosition);
                Packer<FPQuaternion>.Read(packer, ref state.fpRotation);
            }
            else
            {
                Packer<Vector3>.Read(packer, ref state.unityPosition);
                Packer<Quaternion>.Read(packer, ref state.unityRotation);
            }
        }
    }
}
