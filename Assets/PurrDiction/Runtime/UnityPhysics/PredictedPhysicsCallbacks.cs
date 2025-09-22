using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedPhysicsCallbacks : StatelessPredictedIdentity, IPredictedPhysicsCallbacks
    {
        [SerializeField, PurrLock] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        public void RaiseTriggerEnter(GameObject other) => onTriggerEnter?.Invoke(other);

        public void RaiseTriggerExit(GameObject other) => onTriggerExit?.Invoke(other);

        public void RaiseTriggerStay(GameObject other) => onTriggerStay?.Invoke(other);

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts) => onCollisionEnter?.Invoke(other, evContacts);

        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts) => onCollisionExit?.Invoke(other, evContacts);

        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts) => onCollisionStay?.Invoke(other, evContacts);

#if UNITY_PHYSICS_3D

        private void OnCollisionEnter(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }
#endif
    }
}
