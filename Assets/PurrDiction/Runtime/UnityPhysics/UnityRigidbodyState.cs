using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbodyState : IPredictedData<UnityRigidbodyState>
    {
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;

        public override string ToString()
        {
            return $"LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}";
        }

        public void Dispose() { }
    }

    public struct UnityRigidbodyCompressedState : IPackedAuto
    {
        public CompressedVector3 linearVelocity;
        public CompressedVector3 angularVelocity;

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
        }

        public override string ToString()
        {
            return $"UnityRigidbodyCompressedState LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}";
        }
    }

    public struct UnityRigidbodyHalfState : IPackedAuto
    {
        public HalfVector3 linearVelocity;
        public HalfVector3 angularVelocity;

        public UnityRigidbodyHalfState(UnityRigidbodyState state)
        {
            linearVelocity = state.linearVelocity;
            angularVelocity = state.angularVelocity;
        }

        public override string ToString()
        {
            return $"UnityRigidbodyHalfState LinearVelocity: {(Vector3)linearVelocity}\nAngularVelocity: {(Vector3)angularVelocity}";
        }
    }
}
