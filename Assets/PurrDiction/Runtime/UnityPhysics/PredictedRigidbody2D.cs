using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody 2D")]
    public class PredictedRigidbody2D : PredictedIdentity<UnityRigidbody2DState>
    {
        public delegate void OnCollisionDelegate(Collision2D other);
        public delegate void OnTriggerDelegate(Collider2D other);
        
        [SerializeField] private Rigidbody2D _rigidbody;
        
        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;
        
        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

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
        
        private void OnCollisionEnter2D(Collision2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionEnter?.Invoke(other);
        }
        
        private void OnCollisionExit2D(Collision2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionExit?.Invoke(other);
        }
        
        private void OnCollisionStay2D(Collision2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionStay?.Invoke(other);
        }
        
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onTriggerEnter?.Invoke(other);
        }
        
        private void OnTriggerExit2D(Collider2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onTriggerExit?.Invoke(other);
        }
        
        private void OnTriggerStay2D(Collider2D other)
        {
            if (!predictionManager.isSimulating)
                return;

            onTriggerStay?.Invoke(other);
        }
    }
}
