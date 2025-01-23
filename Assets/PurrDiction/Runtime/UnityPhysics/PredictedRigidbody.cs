using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(PredictedTransform))]
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
