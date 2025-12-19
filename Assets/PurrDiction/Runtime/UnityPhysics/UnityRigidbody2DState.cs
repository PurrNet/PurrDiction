using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbody2DState : IPredictedData<UnityRigidbody2DState>
    {
        public Vector2 linearVelocity;
        public float angularVelocity;
        public float linearDamping;
        public RigidbodyType2D bodyType;
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
#else
            linearVelocity = rigidbody.velocity;
#endif
            angularVelocity = rigidbody.angularVelocity;
            linearDamping = rigidbody.linearDamping;
            bodyType = rigidbody.bodyType;
            isSleeping = rigidbody.IsSleeping();
        }
#endif

        public void Dispose() { }
    }
}
