using PurrNet.Packing;
using PurrNet.Pooling;
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
        private readonly Collider[] _overlapBuffer = new Collider[16];

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
                isTrigger = _isTrigger,
                overlappingTriggers = DisposableList<PredictedComponentID>.Create(8)
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

            bool solidHit = false;
            PredictedComponentID solidHitId = default;
            bool sphereCastTriggerHit = false;
            PredictedComponentID sphereCastTriggerHitId = default;
            Vector3 finalPos = pos + state.velocity * delta;

            if (Physics.SphereCast(pos, state.radius, direction, out var hit, totalDistance, _layerMask, QueryTriggerInteraction.Collide))
            {
                bool hitIsTrigger = hit.collider.isTrigger;
                bool weAreTrigger = state.isTrigger;

                if (PredictionManager.TryGetClosestPredictedID(hit.collider.gameObject, out var hitId) &&
                    predictionManager.physics3d != null)
                {
                    if (weAreTrigger || hitIsTrigger)
                    {
                        sphereCastTriggerHit = true;
                        sphereCastTriggerHitId = hitId;
                        if (_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, hit.collider.gameObject, true);
                    }
                    else
                    {
                        solidHit = true;
                        solidHitId = hitId;
                        Vector3 relativeVelocity = state.velocity;
                        if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        {
#if UNITY_6000
                            relativeVelocity -= hit.rigidbody.linearVelocity;
#else
                            relativeVelocity -= hit.rigidbody.velocity;
#endif
                        }

                        if (state.hasLastSolidContact && solidHitId.Equals(state.lastSolidContact))
                        {
                            if (_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, hit.collider.gameObject, false,
                                    hit.point, hit.normal, relativeVelocity);
                        }
                        else
                        {
                            if (_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, hit.collider.gameObject, false,
                                    hit.point, hit.normal, relativeVelocity);
                        }
                    }
                }

                if (!state.isTrigger && !hitIsTrigger)
                {
                    finalPos = hit.point + hit.normal * (state.radius + 0.001f);
                    float normalSpeed = Vector3.Dot(state.velocity, hit.normal);
                    if (normalSpeed < 0)
                    {
                        Vector3 reflected = state.velocity - (1f + _bounciness) * normalSpeed * hit.normal;
                        state.velocity = reflected;
                    }
                }
            }

            if (predictionManager.physics3d != null && state.hasLastSolidContact && (!solidHit || !solidHitId.Equals(state.lastSolidContact)))
            {
                if (_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                {
                    var otherGo = state.lastSolidContact.GetGameObject(predictionManager);
                    if (otherGo != null)
                        predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, otherGo, false);
                }
                state.hasLastSolidContact = false;
            }

            if (solidHit)
            {
                state.lastSolidContact = solidHitId;
                state.hasLastSolidContact = true;
            }

            transform.position = finalPos;

            if (predictionManager.physics3d != null && _eventMask != PhysicsEventMask.None)
            {
                int overlapCount = Physics.OverlapSphereNonAlloc(finalPos, state.radius, _overlapBuffer, _layerMask, QueryTriggerInteraction.Collide);
                var currentOverlaps = DisposableList<PredictedComponentID>.Create(8);

                for (int i = 0; i < overlapCount; i++)
                {
                    var c = _overlapBuffer[i];
                    if (!c.isTrigger || c.transform == transform || !PredictionManager.TryGetClosestPredictedID(c.gameObject, out var pid))
                        continue;
                    bool found = false;
                    for (int j = 0; j < currentOverlaps.Count; j++)
                    {
                        if (currentOverlaps[j].Equals(pid)) { found = true; break; }
                    }
                    if (!found)
                        currentOverlaps.Add(pid);
                }

                if (sphereCastTriggerHit)
                {
                    bool inCurrent = false;
                    for (int j = 0; j < currentOverlaps.Count; j++)
                    {
                        if (currentOverlaps[j].Equals(sphereCastTriggerHitId)) { inCurrent = true; break; }
                    }
                    if (!inCurrent)
                        currentOverlaps.Add(sphereCastTriggerHitId);
                }

                var prev = state.overlappingTriggers;
                for (int i = 0; i < prev.Count; i++)
                {
                    var pid = prev[i];
                    bool inCurrent = false;
                    for (int j = 0; j < currentOverlaps.Count; j++)
                    {
                        if (currentOverlaps[j].Equals(pid)) { inCurrent = true; break; }
                    }
                    if (!inCurrent)
                    {
                        if (_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                        {
                            var otherGo = pid.GetGameObject(predictionManager);
                            if (otherGo != null)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, otherGo, true);
                        }
                    }
                    else
                    {
                        if (_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                        {
                            var otherGo = pid.GetGameObject(predictionManager);
                            if (otherGo != null)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, otherGo, true);
                        }
                    }
                }

                for (int i = 0; i < currentOverlaps.Count; i++)
                {
                    var pid = currentOverlaps[i];
                    bool inPrev = false;
                    for (int j = 0; j < prev.Count; j++)
                    {
                        if (prev[j].Equals(pid)) { inPrev = true; break; }
                    }
                    if (!inPrev)
                    {
                        if (_eventMask.HasFlag(PhysicsEventMask.TriggerEnter) && !(sphereCastTriggerHit && pid.Equals(sphereCastTriggerHitId)))
                        {
                            var otherGo = pid.GetGameObject(predictionManager);
                            if (otherGo != null)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, otherGo, true);
                        }
                    }
                }

                prev.Dispose();
                state.overlappingTriggers = currentOverlaps;
            }
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
                isTrigger = to.isTrigger,
                overlappingTriggers = DisposableList<PredictedComponentID>.Create(8),
                lastSolidContact = default,
                hasLastSolidContact = false
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
