using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbody2DState : IPredictedData<UnityRigidbody2DState>
    {
        public Vector2 linearVelocity;
        public float angularVelocity;
        public float linearDamping;

        public override string ToString()
        {
            return $"(linearVelocity: {linearVelocity}, angularVelocity: {angularVelocity})";
        }

        public void Dispose() { }
    }
}
