using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_3D
    /// <summary>
    /// Stay collision and trigger messages for a <see cref="PredictedRigidbody"/>. These fire
    /// once per touching pair per physics step, so they live apart from the Enter/Exit proxy and
    /// are attached only while a Stay event kind is enabled.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class PredictedRigidbodyStayProxy : MonoBehaviour
    {
        internal PredictedRigidbody target;

        private void OnCollisionStay(Collision other)
        {
            if (target)
                target.HandleCollision(PhysicsEventType.Stay, PhysicsEventMask.CollisionStay, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (target)
                target.HandleTrigger(PhysicsEventType.Stay, PhysicsEventMask.TriggerStay, other);
        }
    }
#endif
}
