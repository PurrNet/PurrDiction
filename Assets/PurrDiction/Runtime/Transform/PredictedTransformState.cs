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

        public void SetPositionAndRotation(FPVector3 position, FPQuaternion rotation)
        {
            isFP = true;
            fpPosition = position;
            fpRotation = rotation;
            unityPosition = position.ToVector3();
            unityRotation = rotation.ToQuaternion();
        }

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            isFP = false;
            unityPosition = position;
            unityRotation = rotation;
            fpPosition = position.ToFPVector3();
            fpRotation = rotation.ToFPQuaternion();
        }

        public void SetPositionAndRotation(Transform trs)
        {
            isFP = false;
            trs.GetPositionAndRotation(out unityPosition, out unityRotation);
            fpPosition = unityPosition.ToFPVector3();
            fpRotation = unityRotation.ToFPQuaternion();
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
                FPVector3 pos = default;
                FPQuaternion rot = default;
                Packer<FPVector3>.Read(packer, ref pos);
                Packer<FPQuaternion>.Read(packer, ref rot);
                state.SetPositionAndRotation(pos, rot);
            }
            else
            {
                Vector3 pos = default;
                Quaternion rot = default;
                Packer<Vector3>.Read(packer, ref pos);
                Packer<Quaternion>.Read(packer, ref rot);
                state.SetPositionAndRotation(pos, rot);
            }
        }
    }
}
