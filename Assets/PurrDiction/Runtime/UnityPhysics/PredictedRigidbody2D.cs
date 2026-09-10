using System;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
#if UNITY_PHYSICS_2D
    [RequireComponent(typeof(Rigidbody2D))]
#endif
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody 2D")]
    public class PredictedRigidbody2D : PredictedIdentity<UnityRigidbody2DState>
    {
        [Tooltip("Fraction of the remaining velocity error corrected per second when using SoftCorrection.")]
        [SerializeField, Min(0f)] private float _softVelocityCorrectionRate = 8f;

#if UNITY_PHYSICS_2D
        public delegate void OnCollisionDelegate(GameObject other, PredictedComponentID otherId,
            DisposableList<Physics2DContactPoint> evContacts);
        public delegate void OnTriggerDelegate(GameObject other, PredictedComponentID otherId);

        [SerializeField] private Rigidbody2D _rigidbody;
        [SerializeField] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        private void Reset()
        {
            _rigidbody = GetComponent<Rigidbody2D>();
        }

        private RigidbodyType2D _defaultBodyType;
        private PredictionPolicy _appliedKinematicPolicy = PredictionPolicy.FullPrediction;
        private bool _replayFrozen;
        private RigidbodyType2D _frozenBodyType;
        private bool _constraintsFrozen;
        private RigidbodyConstraints2D _frozenConstraints;
        private Vector2 _frozenLinearVelocity;
        private float _frozenAngularVelocity;
        private Vector2 _softLinearVelocityError;
        private float _softAngularVelocityError;
        private bool _hasSoftVelocityError;

        private struct AppliedVelocityTotals
        {
            public Vector2 linear;
            public float angular;
        }

        private AppliedCorrectionRing<AppliedVelocityTotals> _appliedRing;
        private Vector2 _appliedLinearTotal;
        private float _appliedAngularTotal;

        public override bool controlsTransformPolicy => true;
        public override bool supportsSoftCorrection => true;

        private void Awake()
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody2D>();
            _defaultBodyType = _rigidbody.bodyType;
        }

        public override void OnPreSetup()
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody2D>();
            if (!_rigidbody)
                return;

            RestoreDefaultPhysicsMode();
            if (_rigidbody.bodyType == RigidbodyType2D.Static)
                return;

            linearVelocity = default;
            angularVelocity = default;
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody2D>();
            if (_rigidbody && !preservesStateOnSetup)
                RestoreDefaultPhysicsMode();

            base.Setup(manager, world, id, owner);

            if (!_rigidbody)
                return;

            if (_replayFrozen)
            {
                if (predictionManager && predictionManager.isReplaying)
                    return;

                ClearStaleFreeze();
            }

            if (!preservesStateOnSetup)
                RestoreDefaultPhysicsMode();
            _appliedKinematicPolicy = PredictionPolicy.FullPrediction;
            ApplyEffectiveKinematic();
        }

        private void ApplyEffectiveKinematic()
        {
            if (isServer || !_rigidbody)
                return;

            var effective = EffectivePolicy();
            if (effective == _appliedKinematicPolicy)
                return;
            var previous = _appliedKinematicPolicy;
            _appliedKinematicPolicy = effective;

            if (_replayFrozen)
                return;

            if (effective == PredictionPolicy.ServerRelay)
                ForceRelayKinematic();
            else if (previous == PredictionPolicy.ServerRelay)
                RestoreAuthoritativePhysicsState();
        }

        internal override void SyncEffectivePolicySideEffects()
        {
            if (TracksEffectivePolicyChanges())
                ApplyEffectiveKinematic();
        }

        private void ForceRelayKinematic()
        {
            _rigidbody.bodyType = RigidbodyType2D.Kinematic;
            linearVelocity = default;
            angularVelocity = 0f;
        }

        private void ClearStaleFreeze()
        {
            _replayFrozen = false;
            if (_constraintsFrozen)
            {
                _rigidbody.constraints = _frozenConstraints;
                _constraintsFrozen = false;
            }
            RestoreDefaultPhysicsMode();
        }

        private void RestoreDefaultPhysicsMode()
        {
            _rigidbody.bodyType = _defaultBodyType;
        }

        private void RestoreAuthoritativePhysicsState()
        {
            ref var state = ref currentState;
            var bodyType = (RigidbodyType2D)state.bodyType;
            _rigidbody.bodyType = bodyType;
#if UNITY_6000
            _rigidbody.linearDamping = state.linearDamping;
#endif

            if (bodyType == RigidbodyType2D.Static)
                return;

            linearVelocity = state.linearVelocity;
            _rigidbody.angularVelocity = state.angularVelocity;
        }

        protected override void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            ClearSoftVelocityCorrection();

            SyncControlledTransformPolicy(predictionPolicy);

            ApplyEffectiveKinematic();
        }

        private void ClearSoftVelocityCorrection()
        {
            _softLinearVelocityError = default;
            _softAngularVelocityError = 0f;
            _hasSoftVelocityError = false;
            _appliedRing?.Clear();
            _appliedLinearTotal = default;
            _appliedAngularTotal = 0f;
        }

        internal override void OnReplayStart()
        {
            if (_replayFrozen)
                return;

            _replayFrozen = true;
            _frozenBodyType = _rigidbody.bodyType;
            _frozenLinearVelocity = linearVelocity;
            _frozenAngularVelocity = angularVelocity;
            _constraintsFrozen = _frozenBodyType == RigidbodyType2D.Dynamic;

            if (_constraintsFrozen)
            {
                _frozenConstraints = _rigidbody.constraints;
                _rigidbody.constraints = RigidbodyConstraints2D.FreezeAll;
            }

            if (_frozenBodyType != RigidbodyType2D.Static)
            {
                linearVelocity = default;
                angularVelocity = 0f;
            }
        }

        internal override void OnReplayEnd()
        {
            if (!_replayFrozen)
                return;

            _replayFrozen = false;

            if (_constraintsFrozen)
            {
                _rigidbody.constraints = _frozenConstraints;
                _constraintsFrozen = false;
            }

            if (!isServer && UsesServerRelayTimeline())
            {
                ForceRelayKinematic();
                return;
            }

            _rigidbody.bodyType = _frozenBodyType;
            if (_frozenBodyType != RigidbodyType2D.Static)
            {
                linearVelocity = _frozenLinearVelocity;
                angularVelocity = _frozenAngularVelocity;
            }
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
                linear = _appliedLinearTotal,
                angular = _appliedAngularTotal
            });
        }

        protected override void OnVerifiedStateReceived(ulong tick, in UnityRigidbody2DState predicted, in UnityRigidbody2DState verified)
        {
            var verifiedBodyType = (RigidbodyType2D) verified.bodyType;
            if (_replayFrozen)
                _frozenBodyType = verifiedBodyType;
            else if (_rigidbody.bodyType != verifiedBodyType)
                _rigidbody.bodyType = verifiedBodyType;
#if UNITY_6000
            _rigidbody.linearDamping = verified.linearDamping;
#endif

            var pendingLinear = verified.linearVelocity - predicted.linearVelocity;
            var pendingAngular = verified.angularVelocity - predicted.angularVelocity;

            if (_appliedRing != null && _appliedRing.TryGetBaseline(tick, out var baseline))
            {
                pendingLinear -= _appliedLinearTotal - baseline.linear;
                pendingAngular -= _appliedAngularTotal - baseline.angular;
            }

            _softLinearVelocityError = pendingLinear;
            _softAngularVelocityError = pendingAngular;
            _hasSoftVelocityError = pendingLinear.sqrMagnitude > 1e-6f ||
                                    pendingAngular * pendingAngular > 1e-6f;
        }

        protected override void Simulate(ref UnityRigidbody2DState state, float delta)
        {
            if (!_hasSoftVelocityError || _rigidbody.bodyType != RigidbodyType2D.Dynamic)
                return;

            float blend = 1f - Mathf.Exp(-Mathf.Max(0f, _softVelocityCorrectionRate) * delta);
            var linearStep = _softLinearVelocityError * blend;
            var angularStep = _softAngularVelocityError * blend;

            _softLinearVelocityError -= linearStep;
            _softAngularVelocityError -= angularStep;
            _appliedLinearTotal += linearStep;
            _appliedAngularTotal += angularStep;

            if (_softLinearVelocityError.sqrMagnitude < 1e-6f &&
                _softAngularVelocityError * _softAngularVelocityError < 1e-6f)
            {
                _softLinearVelocityError = default;
                _softAngularVelocityError = 0f;
                _hasSoftVelocityError = false;
            }

            linearVelocity += linearStep;
            angularVelocity += angularStep;
            state.linearVelocity = linearVelocity;
            state.angularVelocity = angularVelocity;
        }

        public override void ResetState()
        {
            base.ResetState();
            ClearSoftVelocityCorrection();
            _replayFrozen = false;
            if (_rigidbody)
            {
                if (_constraintsFrozen)
                {
                    _rigidbody.constraints = _frozenConstraints;
                    _constraintsFrozen = false;
                }
                RestoreDefaultPhysicsMode();
            }
        }

        public override void ResetInterpolation()
        {
            base.ResetInterpolation();
            ClearSoftVelocityCorrection();
        }

        public void MovePosition(Vector2 position)
        {
            _rigidbody.MovePosition(position);
        }

        public void MoveRotation(Quaternion rotation)
        {
            _rigidbody.MoveRotation(rotation);
        }

        public void MoveRotation(float angle)
        {
            _rigidbody.MoveRotation(angle);
        }

        public void MovePositionAndRotation(Vector2 position, Quaternion rotation)
        {
#if UNITY_6000
            _rigidbody.MovePositionAndRotation(position, rotation);
#else
            _rigidbody.MovePosition(position);
            _rigidbody.MoveRotation(rotation);
#endif
        }

        public void MovePositionAndRotation(Vector2 position, float angle)
        {
#if UNITY_6000
            _rigidbody.MovePositionAndRotation(position, angle);
#else
            _rigidbody.MovePosition(position);
            _rigidbody.MoveRotation(angle);
#endif
        }

        protected override void LateAwake()
        {
            if (predictionManager.physics2d == null)
                _eventMask = PhysicsEventMask.None;
        }

        protected override UnityRigidbody2DState GetInitialState()
        {
            return new UnityRigidbody2DState
            {
                linearVelocity = linearVelocity,
                angularVelocity = angularVelocity,
                bodyType = (int) _rigidbody.bodyType,
                isSleeping = _rigidbody.IsSleeping(),
#if UNITY_6000
                linearDamping = _rigidbody.linearDamping,
#endif
            };
        }

        protected override void GetUnityState(ref UnityRigidbody2DState state)
        {
            if (!isServer && UsesServerRelayTimeline())
                return;

            state.linearVelocity = linearVelocity;
            state.angularVelocity = angularVelocity;
            state.bodyType = (int) _rigidbody.bodyType;
            state.isSleeping = _rigidbody.IsSleeping();
#if UNITY_6000
            state.linearDamping = _rigidbody.linearDamping;
#endif
        }

        protected override void SetUnityState(UnityRigidbody2DState state)
        {
            if (_replayFrozen)
            {
                _frozenBodyType = (RigidbodyType2D) state.bodyType;
                _frozenLinearVelocity = state.linearVelocity;
                _frozenAngularVelocity = state.angularVelocity;
                return;
            }

            if (!isServer && UsesServerRelayTimeline())
                return;

            _rigidbody.bodyType = (RigidbodyType2D) state.bodyType;

            if( _rigidbody.bodyType != RigidbodyType2D.Static)
            {
                linearVelocity = state.linearVelocity;
                angularVelocity = state.angularVelocity;
            }

            if (_rigidbody.IsSleeping() != state.isSleeping)
            {
                if ( state.isSleeping)
                    _rigidbody.Sleep();
                else
                    _rigidbody.WakeUp();
            }
#if UNITY_6000
            _rigidbody.linearDamping = state.linearDamping;
#endif
        }

        private void OnCollisionEnter2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void RaiseTriggerEnter(GameObject other, PredictedComponentID otherId)
            => onTriggerEnter?.Invoke(other, otherId);

        public void RaiseTriggerExit(GameObject other, PredictedComponentID otherId)
            => onTriggerExit?.Invoke(other, otherId);

        public void RaiseTriggerStay(GameObject other, PredictedComponentID otherId)
            => onTriggerStay?.Invoke(other, otherId);

        public void RaiseCollisionEnter(GameObject other, PredictedComponentID otherId,
            DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionEnter?.Invoke(other, otherId, evContacts);
        }

        public void RaiseCollisionExit(GameObject other, PredictedComponentID otherId,
            DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionExit?.Invoke(other, otherId, evContacts);
        }

        public void RaiseCollisionStay(GameObject other, PredictedComponentID otherId,
            DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionStay?.Invoke(other, otherId, evContacts);
        }

        public Vector2 position
        {
            get => _rigidbody.position;
            set => _rigidbody.position = value;
        }

        public float rotation
        {
            get => _rigidbody.rotation;
            set => _rigidbody.rotation = value;
        }

        public Vector2 linearVelocity
        {
            get
            {
#if UNITY_6000
                return _rigidbody.linearVelocity;
#else
                return _rigidbody.velocity;
#endif
            }

            set
            {
                //setting linearVelocity on static Rigidbody2D logs a warning.
                if (_rigidbody.bodyType == RigidbodyType2D.Static)
                    return;

#if UNITY_6000
                _rigidbody.linearVelocity = value;
#else
                _rigidbody.velocity = value;
#endif
            }
        }

        public Vector2 velocity
        {
            get => linearVelocity;
            set => linearVelocity = value;
        }

        public float angularVelocity
        {
            get => _rigidbody.angularVelocity;
            set
            {
                //setting angularVelocity on static Rigidbody2D logs a warning.
                if (_rigidbody.bodyType == RigidbodyType2D.Static)
                    return;

                _rigidbody.angularVelocity = value;
            }
        }

        /// <summary>
        /// Adds a force to the Rigidbody2D.
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
        {
            linearVelocity += mode switch
            {
                ForceMode2D.Force => force / _rigidbody.mass * predictionManager.tickDelta,
                ForceMode2D.Impulse => force / _rigidbody.mass,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        /// <summary>
        /// Adds a torque to the Rigidbody2D.
        /// </summary>
        /// <param name="torque">Torque value in world coordinates.</param>
        /// <param name="mode">Type of torque to apply.</param>
        public void AddTorque(float torque, ForceMode2D mode = ForceMode2D.Force)
        {
            angularVelocity += mode switch
            {
                ForceMode2D.Force => ApplyInverseInertia(torque) * predictionManager.tickDelta,
                ForceMode2D.Impulse => ApplyInverseInertia(torque),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        private float ApplyInverseInertia(float torque)
        {
            var inertia = _rigidbody.inertia;
            return inertia > 0f ? torque / inertia * Mathf.Rad2Deg : 0f;
        }

        /// <summary>
        /// Adds a force to the Rigidbody2D in local coordinates.
        /// </summary>
        /// <param name="force">Force vector in local coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddRelativeForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
        {
            var relativeForce = _rigidbody.transform.TransformDirection(force);
            AddForce(relativeForce, mode);
        }

        /// <summary>
        /// Adds a torque to the Rigidbody2D relative to its local coordinate system.
        /// </summary>
        /// <param name="torque">Torque value in local coordinates.</param>
        /// <param name="mode">Type of torque to apply.</param>
        public void AddRelativeTorque(float torque, ForceMode2D mode = ForceMode2D.Force)
        {
            AddTorque(torque, mode);
        }

        /// <summary>
        /// Applies a force at a specific position, creating both linear and angular motion.
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="position">Position in world coordinates where the force is applied.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForceAtPosition(Vector2 force, Vector2 position, ForceMode2D mode = ForceMode2D.Force)
        {
            AddForce(force, mode);

            Vector2 relativePosition = position - _rigidbody.worldCenterOfMass;
            float torque = relativePosition.x * force.y - relativePosition.y * force.x;
            AddTorque(torque, mode);
        }

        /// <summary>
        /// Applies a force to the Rigidbody2D that simulates an explosion effect.
        /// </summary>
        /// <param name="explosionForce">The force of the explosion.</param>
        /// <param name="explosionPosition">The center of the explosion.</param>
        /// <param name="explosionRadius">The radius of the explosion.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddExplosionForce(float explosionForce, Vector2 explosionPosition, float explosionRadius, ForceMode2D mode = ForceMode2D.Force)
        {
            Vector2 explosionToObject = _rigidbody.position - explosionPosition;
            float distance = explosionToObject.magnitude;

            Vector2 direction = distance > 0.01f ? explosionToObject / distance : Vector2.up;

            float force = explosionForce * (1.0f - Mathf.Clamp01(distance / explosionRadius));

            AddForceAtPosition(direction * force, _rigidbody.position, mode);
        }
#endif
    }
}
