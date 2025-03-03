using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody 2D")]
    public class PredictedRigidbody2D : PredictedIdentity<UnityRigidbody2DState>
    {
        public delegate void OnCollisionDelegate(PredictedRigidbody2D other, DisposableList<Physics2DContactPoint> evContacts);
        public delegate void OnTriggerDelegate(PredictedRigidbody2D other);

        [SerializeField] private Rigidbody2D _rigidbody;
        [SerializeField] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;

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
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void RaiseTriggerEnter(PredictedRigidbody2D other) => onTriggerEnter?.Invoke(other);

        public void RaiseTriggerExit(PredictedRigidbody2D other) => onTriggerExit?.Invoke(other);

        public void RaiseTriggerStay(PredictedRigidbody2D other) => onTriggerStay?.Invoke(other);

        public void RaiseCollisionEnter(PredictedRigidbody2D other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionEnter?.Invoke(other, evContacts);
        }

        public void RaiseCollisionExit(PredictedRigidbody2D other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionExit?.Invoke(other, evContacts);
        }

        public void RaiseCollisionStay(PredictedRigidbody2D other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionStay?.Invoke(other, evContacts);
        }
    }
}
