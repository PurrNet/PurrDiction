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
    }
}
