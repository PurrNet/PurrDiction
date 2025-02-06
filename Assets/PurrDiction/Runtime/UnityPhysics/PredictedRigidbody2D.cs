using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody 2D")]
    public class PredictedRigidbody2D : PredictedIdentity<UnityRigidbody2DState>
    {
        [SerializeField] private Rigidbody2D _rigidbody;

        private void Reset()
        {
            _rigidbody = GetComponent<Rigidbody2D>();
        }

        protected override UnityRigidbody2DState GetInitialState()
        {
            return new UnityRigidbody2DState
            {
                linearVelocity = _rigidbody.linearVelocity,
                angularVelocity = _rigidbody.angularVelocity,
                linearDamping = _rigidbody.linearDamping
            };
        }

        protected override void GetUnityState(ref UnityRigidbody2DState state)
        {
            state.linearVelocity = _rigidbody.linearVelocity;
            state.angularVelocity = _rigidbody.angularVelocity;
            state.linearDamping = _rigidbody.linearDamping;
        }

        protected override void SetUnityState(UnityRigidbody2DState state)
        {
            _rigidbody.linearVelocity = state.linearVelocity;
            _rigidbody.angularVelocity = state.angularVelocity;
            _rigidbody.linearDamping = state.linearDamping;
        }
    }
}
