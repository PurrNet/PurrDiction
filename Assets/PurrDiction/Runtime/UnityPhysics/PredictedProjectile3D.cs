using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_3D
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Physics/Predicted Projectile 3D")]
    public class PredictedProjectile3D : PredictedIdentity<ProjectileState3D>, IPredictedPhysicsCallbacks
    {
        [Tooltip("The gravity applied to the projectile (typically negative Y).")]
        [SerializeField, PurrLock] private float _gravity = 0;

        [Tooltip("Used for bounce behaviour. Bounciness is read at startup; not supported to change at runtime.")]
        [SerializeField, PurrLock] private PhysicsMaterial _physicsMaterial;

        [Tooltip("Radius of the spherical projectile shape. Used for collision casting.")]
        [SerializeField, PurrLock] private float _radius = 0.1f;

        [Tooltip("When true, acts as a trigger (passes through, fires events). When false, solid collision with bounce.")]
        [SerializeField, PurrLock] private bool _isTrigger;

        [Tooltip("Layers to test for collisions. Set to hit only relevant objects.")]
        [SerializeField, PurrLock] private LayerMask _layerMask = ~0;

        [Tooltip("Which collision/trigger events to fire.")]
        [SerializeField, PurrLock] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;

        /// <summary>Extra distance added to SphereCast to avoid tunneling through thin geometry. Multiplied by radius.</summary>
        private const float SafetyMarginFactor = 0.1f;

        public float gravity { get => currentState.gravity; set => currentState.gravity = value; }
        public float radius { get => currentState.radius; set => currentState.radius = value; }
        public bool isTrigger { get => currentState.isTrigger; set => currentState.isTrigger = value; }
        public Vector3 position { get => transform.position; set => transform.position = value; }
        public Vector3 velocity { get => currentState.velocity; set => currentState.velocity = value; }

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        private float _bounciness;

        protected override void LateAwake()
        {
            _bounciness = _physicsMaterial != null ? _physicsMaterial.bounciness : 0;
            if (predictionManager.physics3d == null)
                _eventMask = PhysicsEventMask.None;
        }

        protected override ProjectileState3D GetInitialState()
        {
            return new ProjectileState3D
            {
                velocity = Vector3.zero,
                gravity = _gravity,
                radius = Mathf.Max(_radius, 0.001f),
                isTrigger = _isTrigger
            };
        }

        protected override void Simulate(ref ProjectileState3D state, float delta)
        {
            if (delta <= 0)
                return;

            state.velocity.y += state.gravity * delta;

            float speed = state.velocity.magnitude;
            if (speed < 0.0001f)
                return;

            Vector3 pos = transform.position;
            Vector3 direction = state.velocity / speed;
            float castDistance = speed * delta;
            float safetyMargin = Mathf.Max(state.radius * SafetyMarginFactor, 0.001f);
            float totalDistance = castDistance + safetyMargin;

            var queryTriggerInteraction = QueryTriggerInteraction.Collide;

            if (Physics.SphereCast(pos, state.radius, direction, out var hit, totalDistance, _layerMask, queryTriggerInteraction))
            {
                bool hitIsTrigger = hit.collider.isTrigger;
                bool weAreTrigger = state.isTrigger;

                if (PredictionManager.TryGetClosestPredictedID(hit.collider.gameObject, out _) &&
                    predictionManager.physics3d != null)
                {
                    if (weAreTrigger || hitIsTrigger)
                    {
                        if (_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, hit.collider.gameObject, true);
                    }
                    else
                    {
                        Vector3 relativeVelocity = state.velocity;
                        if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        {
#if UNITY_6000
                            relativeVelocity -= hit.rigidbody.linearVelocity;
#else
                            relativeVelocity -= hit.rigidbody.velocity;
#endif
                        }

                        if (_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, hit.collider.gameObject, false,
                                hit.point, hit.normal, relativeVelocity);
                    }
                }

                if (!state.isTrigger && !hitIsTrigger)
                {
                    // Solid collision: move to hit point and bounce
                    transform.position = hit.point + hit.normal * (state.radius + 0.001f);

                    float normalSpeed = Vector3.Dot(state.velocity, hit.normal);
                    if (normalSpeed < 0)
                    {
                        Vector3 reflected = state.velocity - (1f + _bounciness) * normalSpeed * hit.normal;
                        state.velocity = reflected;
                    }
                    return;
                }
            }

            transform.position = pos + state.velocity * delta;
        }

        public void AddImpulse(Vector3 impulse)
        {
            currentState.velocity += impulse;
        }

        public void RaiseTriggerEnter(GameObject other) => onTriggerEnter?.Invoke(other);
        public void RaiseTriggerExit(GameObject other) => onTriggerExit?.Invoke(other);
        public void RaiseTriggerStay(GameObject other) => onTriggerStay?.Invoke(other);

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts) => onCollisionEnter?.Invoke(other, evContacts);
        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts) => onCollisionExit?.Invoke(other, evContacts);
        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts) => onCollisionStay?.Invoke(other, evContacts);

        protected override ProjectileState3D Interpolate(ProjectileState3D from, ProjectileState3D to, float t)
        {
            return new ProjectileState3D
            {
                velocity = Vector3.Lerp(from.velocity, to.velocity, t),
                gravity = to.gravity,
                radius = to.radius,
                isTrigger = to.isTrigger
            };
        }
    }
#else
    public class PredictedProjectile3D : PredictedIdentity<ProjectileState3D>
    {
        protected override ProjectileState3D GetInitialState() => default;
        protected override void Simulate(ref ProjectileState3D state, float delta) { }
        public void AddImpulse(Vector3 impulse) { }
    }
#endif
}
