using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbody2DState : IPredictedData<UnityRigidbody2DState>
    {
        public Vector2 linearVelocity;
        public float angularVelocity;
        public float linearDamping;
        public int bodyType;
        public bool isSleeping;

        public override string ToString()
        {
            return
                $"LinearVelocity: {linearVelocity}\n" +
                $"AngularVelocity: {angularVelocity}\n" +
                $"BodyType: {bodyType}\n" +
                $"IsSleeping: {isSleeping}";
        }

#if UNITY_PHYSICS_2D
        public UnityRigidbody2DState( Rigidbody2D rigidbody )
        {
#if UNITY_6000
            linearVelocity = rigidbody.linearVelocity;
            linearDamping = rigidbody.linearDamping;
#else
            linearVelocity = rigidbody.velocity;
            linearDamping = default;
#endif
            angularVelocity = rigidbody.angularVelocity;
            bodyType = (int) rigidbody.bodyType;
            isSleeping = rigidbody.IsSleeping();
        }
#endif

        public void Dispose() { }
    }
}
