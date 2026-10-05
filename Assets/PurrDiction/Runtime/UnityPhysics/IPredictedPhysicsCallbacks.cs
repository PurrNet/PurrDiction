using UnityEngine;

namespace PurrNet.Prediction
{
    public interface IPredictedPhysicsCallbacks
    {
        public void RaiseTriggerEnter(GameObject other);

        public void RaiseTriggerExit(GameObject other);

        public void RaiseTriggerStay(GameObject other);

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts);

        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts);

        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts);

        public void RaiseTriggerEnter(PredictedTrigger trigger) { }

        public void RaiseTriggerExit(PredictedTrigger trigger) { }

        public void RaiseTriggerStay(PredictedTrigger trigger) { }

        public void RaiseCollisionEnter(PredictedCollision collision) { }

        public void RaiseCollisionExit(PredictedCollision collision) { }

        public void RaiseCollisionStay(PredictedCollision collision) { }
    }
}
