using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_3D
    /// <summary>
    /// Enter/Exit collision and trigger messages for a <see cref="PredictedRigidbody"/>.
    /// Attached by the rigidbody only while any of those event kinds is enabled.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class PredictedRigidbodyContactProxy : MonoBehaviour
    {
        internal PredictedRigidbody target;

        private void OnCollisionEnter(Collision other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Enter, PhysicsEventMask.CollisionEnter, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Exit, PhysicsEventMask.CollisionExit, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Enter, PhysicsEventMask.TriggerEnter, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Exit, PhysicsEventMask.TriggerExit, other);
        }
    }
#endif
}
