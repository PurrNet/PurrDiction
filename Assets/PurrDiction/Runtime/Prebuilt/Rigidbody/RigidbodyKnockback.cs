using UnityEngine;
using UnityEngine.Serialization;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Knockback")]
    public class RigidbodyKnockback : PredictedIdentity<RigidbodyKnockback.KnockbackData>
    {
#if UNITY_PHYSICS_3D
        [FormerlySerializedAs("rigidbody")]
        [SerializeField] private Rigidbody _rigidbody;
#endif

        [FormerlySerializedAs("offensiveForce")]
        [Tooltip("How much force to apply to others")]
        [SerializeField] private float _offensiveForce = 1;

        [FormerlySerializedAs("receiveMultiplier")]
        [Tooltip("A multiplier to decipher how much of the opposing objects force to apply to self")]
        [SerializeField] private float _receiveMultiplier = 1;

        [FormerlySerializedAs("offsetType")]
        [Tooltip("Whether the offset is in world space or local (followed rotation)")]
        [SerializeField] private OffsetType _offsetType;

        [FormerlySerializedAs("receiveKnockbackOffset")]
        [Tooltip("Offset from object used to calculate where the force is applied from and to - Used for directional calculation")]
        [SerializeField] private Vector3 _receiveKnockbackOffset;

        [FormerlySerializedAs("giveKnockbackOffset")]
        [Tooltip("Offset from object used to calculate where the force is applied from and to - Used for directional calculation")]
        [SerializeField] private Vector3 _giveKnockbackOffset;

#if UNITY_EDITOR
        [FormerlySerializedAs("drawGizmos")]
        [Header("Debug")]
        [SerializeField] private bool _drawGizmos = true;
#endif

#if UNITY_PHYSICS_3D
        private void Reset()
        {
            if(!TryGetComponent(out _rigidbody))
                _rigidbody = gameObject.AddComponent<Rigidbody>();
        }
#endif

        public void Knockback(Vector3 otherPosition, float force)
        {
            Vector3 direction = default;
            switch (_offsetType)
            {
                case OffsetType.Local:
                    direction = (transform.TransformPoint(_receiveKnockbackOffset) - otherPosition).normalized;
                    break;
                case OffsetType.World:
                    direction = (transform.position + _receiveKnockbackOffset - otherPosition).normalized;
                    break;
            }

            var state = currentState;
            state.direction = direction;
            state.force = force * _receiveMultiplier;
            currentState = state;
        }

        protected override void Simulate(ref KnockbackData state, float delta)
        {
            base.Simulate(ref state, delta);

            if(state.force <= 0)
                return;

#if UNITY_PHYSICS_3D
            _rigidbody.AddForce(state.direction * state.force, ForceMode.Impulse);
#endif
            state.direction = default;
            state.force = 0;
        }

#if UNITY_PHYSICS_3D
        private void OnCollisionEnter(Collision other)
        {
            if(other.gameObject.TryGetComponent(out RigidbodyKnockback knockback))
            {
                switch (_offsetType)
                {
                    case OffsetType.Local:
                        knockback.Knockback(transform.TransformPoint(_giveKnockbackOffset), _offensiveForce);
                        break;
                    case OffsetType.World:
                        knockback.Knockback(transform.position + _giveKnockbackOffset, _offensiveForce);
                        break;
                }
            }
        }
#endif

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!_drawGizmos)
                return;

            switch (_offsetType)
            {
                case OffsetType.Local:
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(transform.TransformPoint(_receiveKnockbackOffset), 0.1f);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(transform.TransformPoint(_giveKnockbackOffset), 0.1f);
                    break;
                case OffsetType.World:
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(transform.position + _receiveKnockbackOffset, 0.1f);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(transform.position + _giveKnockbackOffset, 0.1f);
                    break;
            }
        }
#endif


        public struct KnockbackData : IPredictedData<KnockbackData>
        {
            public Vector3 direction;
            public float force;

            public void Dispose() { }
        }

        private enum OffsetType
        {
            Local,
            World
        }
    }
}
