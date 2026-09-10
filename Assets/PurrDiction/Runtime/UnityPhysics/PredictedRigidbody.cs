using System;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Flags]
    public enum PhysicsEventMask
    {
        None = 0,
        CollisionEnter = 1 << 0,
        CollisionExit = 1 << 1,
        CollisionStay = 1 << 2,
        TriggerEnter = 1 << 3,
        TriggerExit = 1 << 4,
        TriggerStay = 1 << 5,
        ControllerColliderHit = 1 << 6
    }

    public enum FloatAccuracy
    {
        Purrfect = 0,
        Medium = 1,
        Low = 2
    }

    public delegate void OnCollisionDelegate(GameObject other, PredictedComponentID otherId,
        PhysicsCollision physicsEvent);
    public delegate void OnTriggerDelegate(GameObject other, PredictedComponentID otherId);
    public delegate void OnControllerColliderHitDelegate(GameObject other, PhysicsControllerHit physicsEvent);

#if UNITY_PHYSICS_3D
    [RequireComponent(typeof(Rigidbody))]
#endif
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody")]
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>, IPredictedPhysicsCallbacks
    {
        [Tooltip("Fraction of the remaining velocity error corrected per second when using SoftCorrection.")]
        [SerializeField, Min(0f)] private float _softVelocityCorrectionRate = 8f;

#if UNITY_PHYSICS_3D
        [SerializeField, PurrLock] private Rigidbody _rigidbody;
        [SerializeField, PurrLock] private FloatAccuracy _floatAccuracy = FloatAccuracy.Medium;
        [SerializeField, PurrLock] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;
        [SerializeField] private bool _ignoreTriggerOnTrigger;
        public new Rigidbody rigidbody => _rigidbody;

        public Rigidbody rb => _rigidbody;

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        public Vector3 position
        {
            get => _rigidbody.position;
            set => _rigidbody.position = value;
        }

        public Quaternion rotation
        {
            get => _rigidbody.rotation;
            set => _rigidbody.rotation = value;
        }

        public Vector3 linearVelocity
        {
#if UNITY_6000
            get => _rigidbody.linearVelocity;
            set
            {
                if (_rigidbody.isKinematic)
                    return;

                _rigidbody.linearVelocity = value;
            }
#else
            get => _rigidbody.velocity;
            set
            {
                if (_rigidbody.isKinematic)
                    return;

                _rigidbody.velocity = value;
            }
#endif
        }

        public Vector3 velocity
        {
            get => linearVelocity;
            set => linearVelocity = value;
        }

        public Vector3 angularVelocity
        {
            get => _rigidbody.angularVelocity;
            set
            {
                if (_rigidbody.isKinematic)
                    return;

                _rigidbody.angularVelocity = value;
            }
        }

        public bool isKinematic
        {
            get => _rigidbody.isKinematic;
            set => _rigidbody.isKinematic = value;
        }
        public bool useGravity
        {
            get => _rigidbody.useGravity;
            set => _rigidbody.useGravity = value;
        }

        private bool _defaultKinematic;
        private CollisionDetectionMode _defaultCollisionMode;
        private PredictionPolicy _appliedKinematicPolicy = PredictionPolicy.FullPrediction;

        private bool _replayFrozen;
        private bool _frozenKinematic;
        private bool _constraintsFrozen;
        private RigidbodyConstraints _frozenConstraints;
        private Vector3 _frozenLinearVelocity;
        private Vector3 _frozenAngularVelocity;
        private Vector3 _softLinearVelocityError;
        private Vector3 _softAngularVelocityError;
        private bool _hasSoftVelocityError;

        private struct AppliedVelocityTotals
        {
            public Vector3 linear;
            public Vector3 angular;
        }

        private AppliedCorrectionRing<AppliedVelocityTotals> _appliedRing;
        private Vector3 _appliedLinearTotal;
        private Vector3 _appliedAngularTotal;

        public override bool controlsTransformPolicy => true;
        public override bool supportsSoftCorrection => true;

        private void Awake()
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody>();
            _defaultKinematic = _rigidbody.isKinematic;
            _defaultCollisionMode = _rigidbody.collisionDetectionMode;
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody>();
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
            if (!_rigidbody.isKinematic)
            {
                linearVelocity = default;
                angularVelocity = default;
            }

            if (_rigidbody.collisionDetectionMode is CollisionDetectionMode.Continuous or CollisionDetectionMode.ContinuousDynamic)
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rigidbody.isKinematic = true;
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
            _rigidbody.isKinematic = _defaultKinematic;
            _rigidbody.collisionDetectionMode = _defaultCollisionMode;
        }

        private void RestoreAuthoritativePhysicsState()
        {
            ref var state = ref currentState;
            _rigidbody.isKinematic = state.isKinematic;
            _rigidbody.collisionDetectionMode = state.isKinematic &&
                                                _defaultCollisionMode is CollisionDetectionMode.Continuous or
                                                    CollisionDetectionMode.ContinuousDynamic
                ? CollisionDetectionMode.ContinuousSpeculative
                : _defaultCollisionMode;
            _rigidbody.useGravity = state.useGravity;

            if (state.isKinematic)
                return;

#if UNITY_6000
            _rigidbody.linearVelocity = state.linearVelocity;
#else
            _rigidbody.velocity = state.linearVelocity;
#endif
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
            _softAngularVelocityError = default;
            _hasSoftVelocityError = false;
            _appliedRing?.Clear();
            _appliedLinearTotal = default;
            _appliedAngularTotal = default;
        }

        internal override void OnReplayStart()
        {
            if (_replayFrozen)
                return;

            _replayFrozen = true;
            _frozenKinematic = _rigidbody.isKinematic;
            _frozenLinearVelocity = linearVelocity;
            _frozenAngularVelocity = angularVelocity;
            _constraintsFrozen = !_frozenKinematic;

            if (_constraintsFrozen)
            {
                _frozenConstraints = _rigidbody.constraints;
                _rigidbody.constraints = RigidbodyConstraints.FreezeAll;
                linearVelocity = default;
                angularVelocity = default;
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

            _rigidbody.isKinematic = _frozenKinematic;
            if (!_frozenKinematic)
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

        protected override void OnVerifiedStateReceived(ulong tick, in UnityRigidbodyState predicted, in UnityRigidbodyState verified)
        {
            if (_replayFrozen)
                _frozenKinematic = verified.isKinematic;
            else if (_rigidbody.isKinematic != verified.isKinematic)
                _rigidbody.isKinematic = verified.isKinematic;
            useGravity = verified.useGravity;

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
                                    pendingAngular.sqrMagnitude > 1e-6f;
        }

        protected override void Simulate(ref UnityRigidbodyState state, float delta)
        {
            if (!_hasSoftVelocityError || _rigidbody.isKinematic)
                return;

            float blend = 1f - Mathf.Exp(-Mathf.Max(0f, _softVelocityCorrectionRate) * delta);
            var linearStep = _softLinearVelocityError * blend;
            var angularStep = _softAngularVelocityError * blend;

            _softLinearVelocityError -= linearStep;
            _softAngularVelocityError -= angularStep;
            _appliedLinearTotal += linearStep;
            _appliedAngularTotal += angularStep;

            if (_softLinearVelocityError.sqrMagnitude < 1e-6f &&
                _softAngularVelocityError.sqrMagnitude < 1e-6f)
            {
                _softLinearVelocityError = default;
                _softAngularVelocityError = default;
                _hasSoftVelocityError = false;
            }

            linearVelocity += linearStep;
            angularVelocity += angularStep;
            state.linearVelocity = linearVelocity;
            state.angularVelocity = angularVelocity;
        }

        public override void OnPreSetup()
        {
            if (!_rigidbody)
                _rigidbody = GetComponent<Rigidbody>();
            if (!_rigidbody)
                return;

            RestoreDefaultPhysicsMode();
            if (_rigidbody.isKinematic)
                return;

            linearVelocity = default;
            angularVelocity = default;
        }

        protected override void LateAwake()
        {
            if (!predictionManager.physics3d)
                _eventMask = PhysicsEventMask.None;
        }

        protected override void WriteDeltaState(BitPacker packer, in UnityRigidbodyState baseline, in UnityRigidbodyState current)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.WriteDeltaState(packer, in baseline, in current);
                    break;
                case FloatAccuracy.Medium:
                    DeltaPacker<UnityRigidbodyCompressedState>.Write(packer,
                        new UnityRigidbodyCompressedState(baseline),
                        new UnityRigidbodyCompressedState(current));
                    break;
                case FloatAccuracy.Low:
                    DeltaPacker<UnityRigidbodyHalfState>.Write(packer,
                        new UnityRigidbodyHalfState(baseline),
                        new UnityRigidbodyHalfState(current));
                    break;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        protected override void ReadDeltaState(BitPacker packer, in UnityRigidbodyState baseline, ref UnityRigidbodyState state)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.ReadDeltaState(packer, in baseline, ref state);
                    break;
                case FloatAccuracy.Medium:
                {
                    UnityRigidbodyCompressedState compressedState = default;
                    DeltaPacker<UnityRigidbodyCompressedState>.Read(packer, new UnityRigidbodyCompressedState(baseline), ref compressedState);

                    state.linearVelocity = compressedState.linearVelocity;
                    state.angularVelocity = compressedState.angularVelocity;
                    state.isKinematic = compressedState.isKinematic;
                    state.isSleeping = compressedState.isSleeping;
                    state.useGravity = compressedState.useGravity;
                    break;
                }
                case FloatAccuracy.Low:
                {
                    UnityRigidbodyHalfState halfState = default;
                    DeltaPacker<UnityRigidbodyHalfState>.Read(packer, new UnityRigidbodyHalfState(baseline), ref halfState);

                    state.linearVelocity = halfState.linearVelocity;
                    state.angularVelocity = halfState.angularVelocity;
                    state.isKinematic = halfState.isKinematic;
                    state.isSleeping = halfState.isSleeping;
                    state.useGravity = halfState.useGravity;
                    break;
                }
                default: throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        ///   <para>Adds a force to the Rigidbody.</para>
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            linearVelocity += mode switch
            {
                ForceMode.Force => force / _rigidbody.mass * predictionManager.tickDelta,
                ForceMode.Acceleration => force * predictionManager.tickDelta,
                ForceMode.Impulse => force / _rigidbody.mass,
                ForceMode.VelocityChange => force,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        /// <summary>
        ///   <para>Adds a torque to the Rigidbody.</para>
        /// </summary>
        /// <param name="torque">Torque vector in world coordinates.</param>
        /// <param name="mode">Type of torque to apply.</param>
        public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            angularVelocity += mode switch
            {
                ForceMode.Force => ApplyInverseInertia(torque) * predictionManager.tickDelta,
                ForceMode.Acceleration => torque * predictionManager.tickDelta,
                ForceMode.Impulse => ApplyInverseInertia(torque),
                ForceMode.VelocityChange => torque,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        private Vector3 ApplyInverseInertia(Vector3 torque)
        {
            var tensorSpace = _rigidbody.rotation * _rigidbody.inertiaTensorRotation;
            var local = Quaternion.Inverse(tensorSpace) * torque;
            var tensor = _rigidbody.inertiaTensor;
            local.x = tensor.x > 0f ? local.x / tensor.x : 0f;
            local.y = tensor.y > 0f ? local.y / tensor.y : 0f;
            local.z = tensor.z > 0f ? local.z / tensor.z : 0f;
            return tensorSpace * local;
        }

        /// <summary>
        ///   <para>Adds a force to the Rigidbody in local coordinates.</para>
        /// </summary>
        /// <param name="force">Force vector in local coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddRelativeForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            AddForce(_rigidbody.rotation * force, mode);
        }

        /// <summary>
        /// Adds a torque to the rigidbody relative to its local coordinate system.
        /// </summary>
        /// <param name="torque">Torque vector in local coordinates.</param>
        /// <param name="mode">Type of torque to apply.</param>
        public void AddRelativeTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            AddTorque(_rigidbody.rotation * torque, mode);
        }

        /// <summary>
        /// Applies a force at a specific position, creating both linear and angular motion.
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="position">Position in world coordinates where the force is applied.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode = ForceMode.Force)
        {
            AddForce(force, mode);

            Vector3 relativePosition = position - _rigidbody.worldCenterOfMass;
            Vector3 torque = Vector3.Cross(relativePosition, force);
            AddTorque(torque, mode);
        }

        /// <summary>
        /// Applies a force to the rigidbody that simulates an explosion effect.
        /// </summary>
        /// <param name="explosionForce">The force of the explosion.</param>
        /// <param name="explosionPosition">The center of the explosion.</param>
        /// <param name="explosionRadius">The radius of the explosion.</param>
        /// <param name="upwardsModifier">Adjustment to the apparent position of the explosion to make it seem to lift objects.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddExplosionForce(float explosionForce, Vector3 explosionPosition, float explosionRadius, float upwardsModifier = 0.0f, ForceMode mode = ForceMode.Force)
        {
            Vector3 explosionToObject = _rigidbody.position - explosionPosition;
            float distance = explosionToObject.magnitude;

            Vector3 direction = distance > 0.01f ? explosionToObject / distance : Vector3.up;

            direction += Vector3.up * upwardsModifier;
            direction.Normalize();

            float force = explosionForce * (1.0f - Mathf.Clamp01(distance / explosionRadius));

            AddForceAtPosition(direction * force, _rigidbody.position, mode);
        }

        private void Reset()
        {
            _rigidbody = GetComponent<Rigidbody>();
        }

        public override void ResetState()
        {
            base.ResetState();
            ClearSoftVelocityCorrection();
            _replayFrozen = false;
            if (_constraintsFrozen)
            {
                _rigidbody.constraints = _frozenConstraints;
                _constraintsFrozen = false;
            }
            RestoreDefaultPhysicsMode();
        }

        public override void ResetInterpolation()
        {
            base.ResetInterpolation();
            ClearSoftVelocityCorrection();
        }

        protected override UnityRigidbodyState GetInitialState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = linearVelocity,
                angularVelocity = angularVelocity,
                isKinematic = isKinematic,
                isSleeping = _rigidbody.IsSleeping(),
                useGravity = useGravity,
            };
        }

        protected override void GetUnityState(ref UnityRigidbodyState state)
        {
            if (!isServer && UsesServerRelayTimeline())
                return;

            state.isKinematic = isKinematic;
            state.linearVelocity = linearVelocity;
            state.angularVelocity = angularVelocity;
            state.isSleeping = _rigidbody.IsSleeping();
            state.useGravity = useGravity;
        }

        protected override void SetUnityState(UnityRigidbodyState state)
        {
            if (_replayFrozen)
            {
                _frozenKinematic = state.isKinematic;
                _frozenLinearVelocity = state.linearVelocity;
                _frozenAngularVelocity = state.angularVelocity;
                useGravity = state.useGravity;
                return;
            }

            if (!isServer && UsesServerRelayTimeline())
            {
                useGravity = state.useGravity;
                return;
            }

            isKinematic = state.isKinematic;
            useGravity = state.useGravity;
            if (!state.isKinematic)
            {
                linearVelocity = state.linearVelocity;
                angularVelocity = state.angularVelocity;
            }

            if (_rigidbody.IsSleeping() != state.isSleeping)
            {
                if (state.isSleeping)
                     _rigidbody.Sleep();
                else _rigidbody.WakeUp();
            }
        }

        private void OnCollisionEnter(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            if (_ignoreTriggerOnTrigger && other.isTrigger)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            if (_ignoreTriggerOnTrigger && other.isTrigger)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager || !predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            if (_ignoreTriggerOnTrigger && other.isTrigger)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void MovePosition(Vector3 position)
        {
            _rigidbody.MovePosition(position);
        }

        public void MoveRotation(Quaternion rotation)
        {
            _rigidbody.MoveRotation(rotation);
        }

        public void Move(Vector3 position, Quaternion rotation)
        {
            _rigidbody.Move(position, rotation);
        }


        public void RaiseTriggerEnter(GameObject other, PredictedComponentID otherId)
        {
            onTriggerEnter?.Invoke(other, otherId);
        }

        public void RaiseTriggerExit(GameObject other, PredictedComponentID otherId)
        {
            onTriggerExit?.Invoke(other, otherId);
        }

        public void RaiseTriggerStay(GameObject other, PredictedComponentID otherId)
        {
            onTriggerStay?.Invoke(other, otherId);
        }

        public void RaiseCollisionEnter(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            onCollisionEnter?.Invoke(other, otherId, evContacts);
        }

        public void RaiseCollisionExit(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            onCollisionExit?.Invoke(other, otherId, evContacts);
        }

        public void RaiseCollisionStay(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            onCollisionStay?.Invoke(other, otherId, evContacts);
        }
#else
        public void RaiseTriggerEnter(GameObject other, PredictedComponentID otherId)
        {
            throw new NotImplementedException();
        }

        public void RaiseTriggerExit(GameObject other, PredictedComponentID otherId)
        {
            throw new NotImplementedException();
        }

        public void RaiseTriggerStay(GameObject other, PredictedComponentID otherId)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionEnter(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionExit(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionStay(GameObject other, PredictedComponentID otherId,
            PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }
#endif
    }
}
