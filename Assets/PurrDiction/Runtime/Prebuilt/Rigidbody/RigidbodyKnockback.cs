using System;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Knockback")]
    public class RigidbodyKnockback : PredictedIdentity<RigidbodyKnockback.KnockbackData>
    {
        [SerializeField] private Rigidbody rigidbody;
        
        [Tooltip("How much force to apply to others")]
        [SerializeField] private float offensiveForce = 1;
        
        [Tooltip("A multiplier to decipher how much of the opposing objects force to apply to self")]
        [SerializeField] private float receiveMultiplier = 1;

        [Tooltip("Whether the offset is in world space or local (followed rotation)")]
        [SerializeField] private OffsetType offsetType;
        
        [Tooltip("Offset from object used to calculate where the force is applied from and to - Used for directional calculation")]
        [SerializeField] private Vector3 receiveKnockbackOffset, giveKnockbackOffset;

#if UNITY_EDITOR
        [Header("Debug")] 
        [SerializeField] private bool drawGizmos = true;
#endif

        private void Reset()
        {
            if(!TryGetComponent(out rigidbody))
                rigidbody = gameObject.AddComponent<Rigidbody>();
        }

        public void Knockback(Vector3 otherPosition, float force)
        {
            Vector3 direction = default;
            switch (offsetType)
            {
                case OffsetType.Local:
                    direction = (transform.TransformPoint(receiveKnockbackOffset) - otherPosition).normalized;
                    break;
                case OffsetType.World:
                    direction = (transform.position + receiveKnockbackOffset - otherPosition).normalized;
                    break;
            }

            var state = currentState;
            state.direction = direction;
            state.force = force * receiveMultiplier;
            currentState = state;
        }

        protected override void Simulate(ref KnockbackData state, FP delta)
        {
            base.Simulate(ref state, delta);
            
            if(state.force <= 0)
                return;
            
            rigidbody.AddForce(state.direction * state.force, ForceMode.Impulse);
            state.direction = default;
            state.force = 0;
        }

        private void OnCollisionEnter(Collision other)
        {
            if(other.gameObject.TryGetComponent(out RigidbodyKnockback knockback))
            {
                switch (offsetType)
                {
                    case OffsetType.Local:
                        knockback.Knockback(transform.TransformPoint(giveKnockbackOffset), offensiveForce);
                        break;
                    case OffsetType.World:
                        knockback.Knockback(transform.position + giveKnockbackOffset, offensiveForce);
                        break;
                }
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;
            
            switch (offsetType)
            {
                case OffsetType.Local:
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(transform.TransformPoint(receiveKnockbackOffset), 0.1f);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(transform.TransformPoint(giveKnockbackOffset), 0.1f);
                    break;
                case OffsetType.World:
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(transform.position + receiveKnockbackOffset, 0.1f);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(transform.position + giveKnockbackOffset, 0.1f);
                    break;
            }
        }
#endif
        

        public struct KnockbackData : IPredictedData<KnockbackData>
        {
            public Vector3 direction;
            public float force;
        }

        private enum OffsetType
        {
            Local,
            World
        }
    }
}
