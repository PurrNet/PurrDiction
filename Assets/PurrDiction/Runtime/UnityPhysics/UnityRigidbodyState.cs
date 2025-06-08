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
}
