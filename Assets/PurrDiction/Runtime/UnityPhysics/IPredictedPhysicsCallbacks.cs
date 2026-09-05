using UnityEngine;

namespace PurrNet.Prediction
{
    public interface IPredictedPhysicsCallbacks
    {
        public void RaiseTriggerEnter(GameObject other, PredictedComponentID otherId);

        public void RaiseTriggerExit(GameObject other, PredictedComponentID otherId);

        public void RaiseTriggerStay(GameObject other, PredictedComponentID otherId);

        public void RaiseCollisionEnter(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts);

        public void RaiseCollisionExit(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts);

        public void RaiseCollisionStay(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts);
    }
}
