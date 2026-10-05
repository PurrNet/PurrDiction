using System;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedPhysicsCallbacks : StatelessPredictedIdentity, IPredictedPhysicsCallbacks
    {
        [SerializeField, PurrLock] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x7F;

        [Obsolete("Use onPredictedCollisionEnter. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnCollisionDelegate onCollisionEnter;
        [Obsolete("Use onPredictedCollisionExit. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnCollisionDelegate onCollisionExit;
        [Obsolete("Use onPredictedCollisionStay. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnCollisionDelegate onCollisionStay;

        [Obsolete("Use onPredictedTriggerEnter. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnTriggerDelegate onTriggerEnter;
        [Obsolete("Use onPredictedTriggerExit. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnTriggerDelegate onTriggerExit;
        [Obsolete("Use onPredictedTriggerStay. It also carries the other object's PredictedComponentID and, on Exit, still fires after the other object was deleted.")]
        public event OnTriggerDelegate onTriggerStay;

        public event OnPredictedCollisionDelegate onPredictedCollisionEnter;
        public event OnPredictedCollisionDelegate onPredictedCollisionExit;
        public event OnPredictedCollisionDelegate onPredictedCollisionStay;

        public event OnPredictedTriggerDelegate onPredictedTriggerEnter;
        public event OnPredictedTriggerDelegate onPredictedTriggerExit;
        public event OnPredictedTriggerDelegate onPredictedTriggerStay;

        public event OnControllerColliderHitDelegate onControllerColliderHit;

#pragma warning disable CS0618 // the GameObject-only events stay raised until they are removed
        public void RaiseTriggerEnter(GameObject other) => onTriggerEnter?.Invoke(other);

        public void RaiseTriggerExit(GameObject other) => onTriggerExit?.Invoke(other);

        public void RaiseTriggerStay(GameObject other) => onTriggerStay?.Invoke(other);

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts) => onCollisionEnter?.Invoke(other, evContacts);

        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts) => onCollisionExit?.Invoke(other, evContacts);

        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts) => onCollisionStay?.Invoke(other, evContacts);
#pragma warning restore CS0618

        public void RaiseTriggerEnter(PredictedTrigger trigger) => onPredictedTriggerEnter?.Invoke(trigger);

        public void RaiseTriggerExit(PredictedTrigger trigger) => onPredictedTriggerExit?.Invoke(trigger);

        public void RaiseTriggerStay(PredictedTrigger trigger) => onPredictedTriggerStay?.Invoke(trigger);

        public void RaiseCollisionEnter(PredictedCollision collision) => onPredictedCollisionEnter?.Invoke(collision);

        public void RaiseCollisionExit(PredictedCollision collision) => onPredictedCollisionExit?.Invoke(collision);

        public void RaiseCollisionStay(PredictedCollision collision) => onPredictedCollisionStay?.Invoke(collision);

        public void RaiseControllerColliderHit(GameObject other, PhysicsControllerHit hit)
            => onControllerColliderHit?.Invoke(other, hit);

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

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.ControllerColliderHit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterControllerColliderHit(this, hit);
        }
#endif
    }
}
