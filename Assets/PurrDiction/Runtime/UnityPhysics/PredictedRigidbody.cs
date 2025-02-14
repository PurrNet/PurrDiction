using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody")]
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>
    {
        public delegate void OnCollisionDelegate(Collision other);
        public delegate void OnTriggerDelegate(Collider other);
        
        [SerializeField] private Rigidbody _rigidbody;
        
        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;
        
        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;
        
        private void Reset()
        {
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
        
        private void OnCollisionEnter(Collision other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionEnter?.Invoke(other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionExit?.Invoke(other);
        }
        
        private void OnCollisionStay(Collision other)
        {
            if (!predictionManager.isSimulating)
                return;

            onCollisionStay?.Invoke(other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!predictionManager.isSimulating)
                return;
            
            onTriggerEnter?.Invoke(other);
        }
        
        private void OnTriggerExit(Collider other)
        {
            if (!predictionManager.isSimulating)
                return;
            
            onTriggerExit?.Invoke(other);
        }
        
        private void OnTriggerStay(Collider other)
        {
            if (!predictionManager.isSimulating)
                return;

            onTriggerStay?.Invoke(other);
        }
    }
}
