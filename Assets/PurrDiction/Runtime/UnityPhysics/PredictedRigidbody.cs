using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct UnityRigidbodyState : IPredictedData<UnityRigidbodyState>
    {
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;

        public override string ToString()
        {
            return $"(linearVelocity: {linearVelocity}, angularVelocity: {angularVelocity})";
        }
    }
    
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>
    {
        [SerializeField] private Rigidbody _rigidbody;

        private void Reset()
        {
            _updateView = false;
            _rigidbody = GetComponent<Rigidbody>();
        }

        protected override UnityRigidbodyState GetInitialState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = _rigidbody.linearVelocity,
                angularVelocity = _rigidbody.angularVelocity
            };
        }

        protected override void GetUnityState(ref UnityRigidbodyState state)
        {
            state.linearVelocity = _rigidbody.linearVelocity;
            state.angularVelocity = _rigidbody.angularVelocity;
        }

        protected override void SetUnityState(UnityRigidbodyState state)
        {
            _rigidbody.linearVelocity = state.linearVelocity;
            _rigidbody.angularVelocity = state.angularVelocity;
        }
    }
}
