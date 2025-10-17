using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbodyState : IPredictedData<UnityRigidbodyState>
    {
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;
        public bool isKinematic;
        public bool isSleeping;

        public override string ToString()
        {
            return $"LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}\nIsKinematic: {isKinematic}\nIsSleeping: {isSleeping}";
        }

#if UNITY_PHYSICS_3D
        public UnityRigidbodyState(Rigidbody rigidbody)
        {
#if UNITY_6000
            linearVelocity = rigidbody.linearVelocity;
#else
            linearVelocity = rigidbody.velocity;
#endif
            angularVelocity = rigidbody.angularVelocity;
            isKinematic = rigidbody.isKinematic;
            isSleeping = rigidbody.IsSleeping();
        }
#endif
        public void Dispose() { }
    }

    public struct UnityRigidbodyCompressedState : IPackedAuto
    {
        public CompressedVector3 linearVelocity;
        public CompressedVector3 angularVelocity;
        public bool isKinematic;
        public bool isSleeping;

        public UnityRigidbodyCompressedState(UnityRigidbodyState state)
        {
            linearVelocity = new CompressedVector3(
                new CompressedFloat(state.linearVelocity.x),
                new CompressedFloat(state.linearVelocity.y),
                new CompressedFloat(state.linearVelocity.z)
            );

            angularVelocity = new CompressedVector3(
                new CompressedFloat(state.angularVelocity.x),
                new CompressedFloat(state.angularVelocity.y),
                new CompressedFloat(state.angularVelocity.z)
            );

            isKinematic = state.isKinematic;
			isSleeping = state.isSleeping;
        }

        public override string ToString()
        {
            return $"MediumState LinearVelocity: {linearVelocity}\nAngularVelocity: {angularVelocity}\nIsKinematic: {isKinematic}\nIsSleeping: {isSleeping}";
        }
    }

    public struct UnityRigidbodyHalfState : IPackedAuto
    {
        public HalfVector3 linearVelocity;
        public HalfVector3 angularVelocity;
        public bool isKinematic;
        public bool isSleeping;

        public UnityRigidbodyHalfState(UnityRigidbodyState state)
        {
            linearVelocity = state.linearVelocity;
            angularVelocity = state.angularVelocity;
            isKinematic = state.isKinematic;
			isSleeping = state.isSleeping;
        }

        public override string ToString()
        {
            return $"HalfState LinearVelocity: {(Vector3)linearVelocity}\nAngularVelocity: {(Vector3)angularVelocity}\nIsKinematic: {isKinematic}\nIsSleeping: {isSleeping}";
        }
    }
}
