using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_2D
    /// <summary>
    /// Enter/Exit collision and trigger messages for a <see cref="PredictedRigidbody2D"/>.
    /// Attached by the rigidbody only while any of those event kinds is enabled.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class PredictedRigidbody2DContactProxy : MonoBehaviour
    {
        internal PredictedRigidbody2D target;

        private void OnCollisionEnter2D(Collision2D other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Enter, PhysicsEventMask.CollisionEnter, other);
        }

        private void OnCollisionExit2D(Collision2D other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Exit, PhysicsEventMask.CollisionExit, other);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Enter, PhysicsEventMask.TriggerEnter, other);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Exit, PhysicsEventMask.TriggerExit, other);
        }
    }
#endif
}
