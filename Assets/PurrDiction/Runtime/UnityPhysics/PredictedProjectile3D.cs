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
        [Tooltip("Fraction of the remaining velocity error corrected per second when using SoftCorrection.")]
        [SerializeField, Min(0f)] private float _softVelocityCorrectionRate = 8f;

        [Tooltip("The gravity applied to the projectile (typically negative Y).")]
        [SerializeField, PurrLock] private float _gravity = 0;

        [Tooltip("Used for bounce behaviour. Bounciness is read at startup; not supported to change at runtime.")]
#if UNITY_6000
        [SerializeField, PurrLock] private PhysicsMaterial _physicsMaterial;
#else
        [SerializeField, PurrLock] private PhysicMaterial _physicsMaterial;
#endif

        [Tooltip("Radius of the spherical projectile shape. Used for collision casting.")]
        [SerializeField, PurrLock] private float _radius = 0.1f;

        [Tooltip("When true, acts as a trigger (passes through, fires events). When false, solid collision with bounce.")]
        [SerializeField, PurrLock] private bool _isTrigger;

        [Tooltip("Layers to test for collisions. Set to hit only relevant objects.")]
        [SerializeField, PurrLock] private LayerMask _layerMask = ~0;

        [Tooltip("Which collision/trigger events to fire.")]
        [SerializeField, PurrLock] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;

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
        private Vector3 _softVelocityError;
        private Vector3 _appliedVelocityTotal;
        private bool _hasSoftVelocityError;

        private struct AppliedVelocityTotals
        {
            public Vector3 velocity;
        }

        private AppliedCorrectionRing<AppliedVelocityTotals> _appliedRing;

        public override bool controlsTransformPolicy => true;
        public override bool supportsSoftCorrection => true;

        public override void ResetState()
        {
            base.ResetState();
            ClearSoftVelocityCorrection();
        }

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
                overlappingTriggers = default
            };
        }

        protected override void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            ClearSoftVelocityCorrection();
            SyncControlledTransformPolicy(predictionPolicy);
        }

        public override void ResetInterpolation()
        {
            base.ResetInterpolation();
            ClearSoftVelocityCorrection();
        }

        private void ClearSoftVelocityCorrection()
        {
            _softVelocityError = default;
            _appliedVelocityTotal = default;
            _hasSoftVelocityError = false;
            _appliedRing?.Clear();
        }

        internal override void SaveStateInHistory(ulong tick)
        {
            base.SaveStateInHistory(tick);

            if (isServer || !UsesSoftCorrectionTimeline())
                return;

            _appliedRing ??= new AppliedCorrectionRing<AppliedVelocityTotals>(
                Mathf.Max(1, predictionManager.tickRate * 10));
            _appliedRing.Record(tick, new AppliedVelocityTotals
            {
                velocity = _appliedVelocityTotal
            });
        }

        protected override void OnVerifiedStateReceived(ulong tick, in ProjectileState3D predicted, in ProjectileState3D verified)
        {
            var pendingVelocity = verified.velocity - predicted.velocity;

            if (_appliedRing != null && _appliedRing.TryGetBaseline(tick, out var baseline))
                pendingVelocity -= _appliedVelocityTotal - baseline.velocity;

            _softVelocityError = pendingVelocity;
            _hasSoftVelocityError = pendingVelocity.sqrMagnitude > 1e-6f;

            ref var live = ref currentState;
            live.gravity = verified.gravity;
            live.radius = verified.radius;
            live.isTrigger = verified.isTrigger;
            live.lastSolidContact = verified.lastSolidContact;
            live.hasLastSolidContact = verified.hasLastSolidContact;
            live.overlappingTriggers.Dispose();
            live.overlappingTriggers = verified.overlappingTriggers.Duplicate();
        }

        private void ApplySoftVelocityCorrection(ref ProjectileState3D state, float delta)
        {
            if (!_hasSoftVelocityError)
                return;

            float blend = 1f - Mathf.Exp(-Mathf.Max(0f, _softVelocityCorrectionRate) * delta);
            var velocityStep = _softVelocityError * blend;

            _softVelocityError -= velocityStep;
            _appliedVelocityTotal += velocityStep;

            if (_softVelocityError.sqrMagnitude < 1e-6f)
            {
                _softVelocityError = default;
                _hasSoftVelocityError = false;
            }

            state.velocity += velocityStep;
        }

        protected override void Simulate(ref ProjectileState3D state, float delta)
        {
            if (delta <= 0)
                return;

            ApplySoftVelocityCorrection(ref state, delta);

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
                    predictionManager.physics3d != null && !hitId.Equals(id))
                {
                    if (weAreTrigger || hitIsTrigger)
                    {
                        sphereCastTriggerHit = true;
                        sphereCastTriggerHitId = hitId;
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
                            if ((_eventMask & PhysicsEventMask.CollisionStay) != 0)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, hit.collider.gameObject, false,
                                    hit.point, hit.normal, relativeVelocity);
                        }
                        else
                        {
                            if ((_eventMask & PhysicsEventMask.CollisionEnter) != 0)
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
                if ((_eventMask & PhysicsEventMask.CollisionExit) != 0)
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
                DisposableList<PredictedComponentID> currentOverlaps = default;

                for (int i = 0; i < overlapCount; i++)
                {
                    var c = _overlapBuffer[i];
                    if (!c.isTrigger || !PredictionManager.TryGetClosestPredictedID(c.gameObject, out var pid) || pid.Equals(id))
                        continue;
                    bool found = false;
                    int currentCount = currentOverlaps.isDisposed ? 0 : currentOverlaps.Count;
                    for (int j = 0; j < currentCount; j++)
                    {
                        if (currentOverlaps[j].Equals(pid)) { found = true; break; }
                    }
                    if (!found)
                    {
                        if (currentOverlaps.isDisposed)
                            currentOverlaps = DisposableList<PredictedComponentID>.Create(8);
                        currentOverlaps.Add(pid);
                    }
                }

                if (sphereCastTriggerHit)
                {
                    bool inCurrent = false;
                    int currentCount = currentOverlaps.isDisposed ? 0 : currentOverlaps.Count;
                    for (int j = 0; j < currentCount; j++)
                    {
                        if (currentOverlaps[j].Equals(sphereCastTriggerHitId)) { inCurrent = true; break; }
                    }
                    if (!inCurrent)
                    {
                        if (currentOverlaps.isDisposed)
                            currentOverlaps = DisposableList<PredictedComponentID>.Create(8);
                        currentOverlaps.Add(sphereCastTriggerHitId);
                    }
                }

                var prev = state.overlappingTriggers;
                int prevCount = prev.isDisposed ? 0 : prev.Count;
                for (int i = 0; i < prevCount; i++)
                {
                    var pid = prev[i];
                    bool inCurrent = false;
                    int currentCount = currentOverlaps.isDisposed ? 0 : currentOverlaps.Count;
                    for (int j = 0; j < currentCount; j++)
                    {
                        if (currentOverlaps[j].Equals(pid)) { inCurrent = true; break; }
                    }
                    if (!inCurrent)
                    {
                        if ((_eventMask & PhysicsEventMask.TriggerExit) != 0)
                        {
                            var otherGo = pid.GetGameObject(predictionManager);
                            if (otherGo != null)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, otherGo, true);
                        }
                    }
                    else
                    {
                        if ((_eventMask & PhysicsEventMask.TriggerStay) != 0)
                        {
                            var otherGo = pid.GetGameObject(predictionManager);
                            if (otherGo != null)
                                predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, otherGo, true);
                        }
                    }
                }

                int newCount = currentOverlaps.isDisposed ? 0 : currentOverlaps.Count;
                for (int i = 0; i < newCount; i++)
                {
                    var pid = currentOverlaps[i];
                    bool inPrev = false;
                    for (int j = 0; j < prevCount; j++)
                    {
                        if (prev[j].Equals(pid)) { inPrev = true; break; }
                    }
                    if (!inPrev && (_eventMask & PhysicsEventMask.TriggerEnter) != 0)
                    {
                        var otherGo = pid.GetGameObject(predictionManager);
                        if (otherGo != null)
                            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, otherGo, true);
                    }
                }

                prev.Dispose();
                state.overlappingTriggers = currentOverlaps;
            }
        }

        public void AddImpulse(Vector3 impulse)
        {
            if (SkipsReplaySpawnInitialization())
                return;

            currentState.velocity += impulse;
        }

        public void RaiseTriggerEnter(GameObject other, PredictedComponentID otherId)
            => onTriggerEnter?.Invoke(other, otherId);

        public void RaiseTriggerExit(GameObject other, PredictedComponentID otherId)
            => onTriggerExit?.Invoke(other, otherId);

        public void RaiseTriggerStay(GameObject other, PredictedComponentID otherId)
            => onTriggerStay?.Invoke(other, otherId);

        public void RaiseCollisionEnter(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
            => onCollisionEnter?.Invoke(other, otherId, evContacts);

        public void RaiseCollisionExit(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
            => onCollisionExit?.Invoke(other, otherId, evContacts);

        public void RaiseCollisionStay(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
            => onCollisionStay?.Invoke(other, otherId, evContacts);

        protected override ProjectileState3D Interpolate(ProjectileState3D from, ProjectileState3D to, float t)
        {
            return new ProjectileState3D
            {
                velocity = Vector3.Lerp(from.velocity, to.velocity, t),
                gravity = to.gravity,
                radius = to.radius,
                isTrigger = to.isTrigger,
                overlappingTriggers = default,
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
