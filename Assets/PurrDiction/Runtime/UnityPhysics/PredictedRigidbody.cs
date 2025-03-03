using System;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Flags]
    public enum PhysicsEventMask
    {
        None = 0,
        CollisionEnter = 1 << 0,
        CollisionExit = 1 << 1,
        CollisionStay = 1 << 2,
        TriggerEnter = 1 << 3,
        TriggerExit = 1 << 4,
        TriggerStay = 1 << 5
    }

    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody")]
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>
    {
        public delegate void OnCollisionDelegate(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts);
        public delegate void OnTriggerDelegate(PredictedRigidbody other);

        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;
        public new Rigidbody rigidbody => _rigidbody;

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
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void RaiseTriggerEnter(PredictedRigidbody other)
        {
            onTriggerEnter?.Invoke(other);
        }

        public void RaiseTriggerExit(PredictedRigidbody other)
        {
            onTriggerExit?.Invoke(other);
        }

        public void RaiseTriggerStay(PredictedRigidbody other)
        {
            onTriggerStay?.Invoke(other);
        }

        public void RaiseCollisionEnter(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionEnter?.Invoke(other, evContacts);
        }

        public void RaiseCollisionExit(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionExit?.Invoke(other, evContacts);
        }

        public void RaiseCollisionStay(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionStay?.Invoke(other, evContacts);
        }
    }
}
