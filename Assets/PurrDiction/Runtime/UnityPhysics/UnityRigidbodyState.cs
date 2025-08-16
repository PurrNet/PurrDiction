using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbodyState : IPredictedData<UnityRigidbodyState>
    {
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;
        public bool isKinematic;

        public override string ToString()
        {
            return $"LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}\nIsKinematic: {isKinematic}";
        }

        public void Dispose() { }
    }

    public struct UnityRigidbodyCompressedState : IPackedAuto
    {
        public CompressedVector3 linearVelocity;
        public CompressedVector3 angularVelocity;
        public bool isKinematic;

        public UnityRigidbodyCompressedState(UnityRigidbodyState state)
        {
            linearVelocity = new CompressedVector3(
                new CompressedFloat(state.linearVelocity.x).Round(),
                new CompressedFloat(state.linearVelocity.y).Round(),
                new CompressedFloat(state.linearVelocity.z).Round()
            );

            angularVelocity = new CompressedVector3(
                new CompressedFloat(state.angularVelocity.x).Round(),
                new CompressedFloat(state.angularVelocity.y).Round(),
                new CompressedFloat(state.angularVelocity.z).Round()
            );

            isKinematic = state.isKinematic;
        }

        public override string ToString()
        {
            return $"UnityRigidbodyCompressedState LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}\nIsKinematic: {isKinematic}";
        }
    }

    public struct UnityRigidbodyHalfState : IPackedAuto
    {
        public HalfVector3 linearVelocity;
        public HalfVector3 angularVelocity;
        public bool isKinematic;

        public UnityRigidbodyHalfState(UnityRigidbodyState state)
        {
            linearVelocity = state.linearVelocity;
            angularVelocity = state.angularVelocity;
            isKinematic = state.isKinematic;
        }

        public override string ToString()
        {
            return $"UnityRigidbodyHalfState LinearVelocity: {(Vector3)linearVelocity}\nAngularVelocity: {(Vector3)angularVelocity}\nIsKinematic: {isKinematic}";
        }
    }
}
