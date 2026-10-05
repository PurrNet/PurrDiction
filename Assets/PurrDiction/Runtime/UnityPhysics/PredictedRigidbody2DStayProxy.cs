using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_2D
    /// <summary>
    /// Stay collision and trigger messages for a <see cref="PredictedRigidbody2D"/>, attached
    /// only while a Stay event kind is enabled. See <see cref="PredictedRigidbodyStayProxy"/>.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class PredictedRigidbody2DStayProxy : MonoBehaviour
    {
        internal PredictedRigidbody2D target;

        private void OnCollisionStay2D(Collision2D other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Stay, PhysicsEventMask.CollisionStay, other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Stay, PhysicsEventMask.TriggerStay, other);
        }
    }
#endif
}
