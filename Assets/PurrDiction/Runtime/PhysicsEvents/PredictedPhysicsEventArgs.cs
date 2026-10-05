using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Payload of a predicted trigger event.
    /// </summary>
    /// <remarks>
    /// <see cref="other"/> is null when the other object was already deleted from the predicted
    /// hierarchy, which happens on the Exit raised by that deletion. <see cref="otherId"/> is always
    /// the id the other object had when the contact was recorded, so it stays usable for bookkeeping
    /// keyed by id even after the GameObject is gone.
    /// </remarks>
    public struct PredictedTrigger
    {
        public GameObject other;
        public PredictedComponentID otherId;

        public PredictedTrigger(GameObject other, PredictedComponentID otherId)
        {
            this.other = other;
            this.otherId = otherId;
        }
    }

    /// <summary>
    /// Payload of a predicted 3D collision event. See <see cref="PredictedTrigger"/> for the
    /// <see cref="other"/> / <see cref="otherId"/> contract.
    /// </summary>
    public struct PredictedCollision
    {
        public GameObject other;
        public PredictedComponentID otherId;
        public PhysicsCollision collision;

        public PredictedCollision(GameObject other, PredictedComponentID otherId, PhysicsCollision collision)
        {
            this.other = other;
            this.otherId = otherId;
            this.collision = collision;
        }
    }

    /// <summary>
    /// Payload of a predicted 2D collision event. See <see cref="PredictedTrigger"/> for the
    /// <see cref="other"/> / <see cref="otherId"/> contract.
    /// </summary>
    public struct PredictedCollision2D
    {
        public GameObject other;
        public PredictedComponentID otherId;
        public DisposableList<Physics2DContactPoint> contacts;

        public PredictedCollision2D(GameObject other, PredictedComponentID otherId,
            DisposableList<Physics2DContactPoint> contacts)
        {
            this.other = other;
            this.otherId = otherId;
            this.contacts = contacts;
        }
    }

    public delegate void OnPredictedTriggerDelegate(PredictedTrigger trigger);
    public delegate void OnPredictedCollisionDelegate(PredictedCollision collision);
    public delegate void OnPredictedCollision2DDelegate(PredictedCollision2D collision);
}
