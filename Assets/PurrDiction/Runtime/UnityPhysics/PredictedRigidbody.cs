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
        TriggerStay = 1 << 5
    }

    public enum FloatAccuracy
    {
        Purrfect = 0,
        Medium = 1,
        Low = 2
    }

    public delegate void OnCollisionDelegate(GameObject other, PhysicsCollision physicsEvent);
    public delegate void OnTriggerDelegate(GameObject other);

#if UNITY_PHYSICS_3D
    [RequireComponent(typeof(Rigidbody))]
#endif
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody")]
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>, IPredictedPhysicsCallbacks
    {
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
            set => _rigidbody.linearVelocity = value;
#else
            get => _rigidbody.velocity;
            set => _rigidbody.velocity = value;
#endif
        }

        public Vector3 velocity
        {
#if UNITY_6000
            get => _rigidbody.linearVelocity;
            set => _rigidbody.linearVelocity = value;
#else
            get => _rigidbody.velocity;
            set => _rigidbody.velocity = value;
#endif
        }

        public Vector3 angularVelocity
        {
            get => _rigidbody.angularVelocity;
            set => _rigidbody.angularVelocity = value;
        }

        public bool isKinematic
        {
            get => _rigidbody.isKinematic;
            set => _rigidbody.isKinematic = value;
        }

        private bool _defaultKinematic;

        private void Awake()
        {
            _defaultKinematic = _rigidbody.isKinematic;
        }

        public override void OnPreSetup()
        {
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

        protected override bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    return base.WriteDeltaState(target, packer, deltaModule);
                case FloatAccuracy.Medium:
                {
                    var key = new DeltaKey<UnityRigidbodyCompressedState>(id);
                    return deltaModule.WriteReliable(packer, target, key, new UnityRigidbodyCompressedState(currentState));
                }
                case FloatAccuracy.Low:
                {
                    var key = new DeltaKey<UnityRigidbodyHalfState>(id);
                    var res = deltaModule.WriteReliable(packer, target, key, new UnityRigidbodyHalfState(currentState));
                    return res;
                }
                default: throw new ArgumentOutOfRangeException();
            }
        }

        protected override void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, ref UnityRigidbodyState state)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.ReadDeltaState(packer, deltaModule, ref state);
                    break;
                case FloatAccuracy.Medium:
                {
                    var key = new DeltaKey<UnityRigidbodyCompressedState>(id);
                    UnityRigidbodyCompressedState compressedState = default;
                    deltaModule.ReadReliable(packer, key, ref compressedState);

                    state.linearVelocity = compressedState.linearVelocity;
                    state.angularVelocity = compressedState.angularVelocity;
                    state.isKinematic = compressedState.isKinematic;
                    state.isSleeping = compressedState.isSleeping;
                    break;
                }
                case FloatAccuracy.Low:
                {
                    var key = new DeltaKey<UnityRigidbodyHalfState>(id);
                    UnityRigidbodyHalfState halfState = default;
                    deltaModule.ReadReliable(packer, key, ref halfState);

                    state.linearVelocity = halfState.linearVelocity;
                    state.angularVelocity = halfState.angularVelocity;
                    state.isKinematic = halfState.isKinematic;
                    state.isSleeping = halfState.isSleeping;
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
            _rigidbody.angularVelocity += mode switch
            {
                ForceMode.Force => torque / _rigidbody.mass * predictionManager.tickDelta,
                ForceMode.Acceleration => torque * predictionManager.tickDelta,
                ForceMode.Impulse => torque / _rigidbody.mass,
                ForceMode.VelocityChange => torque,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        /// <summary>
        ///   <para>Adds a force to the Rigidbody in local coordinates.</para>
        /// </summary>
        /// <param name="force">Force vector in local coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddRelativeForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            var relativeForce = _rigidbody.transform.TransformVector(force);
            AddForce(relativeForce, mode);
        }

        /// <summary>
        /// Adds a torque to the rigidbody relative to its local coordinate system.
        /// </summary>
        /// <param name="torque">Torque vector in local coordinates.</param>
        /// <param name="mode">Type of torque to apply.</param>
        public void AddRelativeTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            var relativeTorque = _rigidbody.transform.TransformVector(torque);
            AddTorque(relativeTorque, mode);
        }

        /// <summary>
        /// Applies a force at a specific position, creating both linear and angular motion.
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="position">Position in world coordinates where the force is applied.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode = ForceMode.Force)
        {
            // Apply linear force
            AddForce(force, mode);

            // Calculate and apply torque
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

            // Normalize without division by zero
            Vector3 direction = distance > 0.01f ? explosionToObject / distance : Vector3.up;

            // Add upward modifier
            direction += Vector3.up * upwardsModifier;
            direction.Normalize();

            // Calculate force based on distance
            float force = explosionForce * (1.0f - Mathf.Clamp01(distance / explosionRadius));

            // Apply force
            AddForceAtPosition(direction * force, _rigidbody.position, mode);
        }

        private void Reset()
        {
            _rigidbody = GetComponent<Rigidbody>();
        }

        public override void ResetState()
        {
            base.ResetState();
            _rigidbody.isKinematic = _defaultKinematic;
        }

        protected override UnityRigidbodyState GetInitialState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = linearVelocity,
                angularVelocity = angularVelocity,
                isKinematic = isKinematic,
                isSleeping = _rigidbody.IsSleeping()
            };
        }

        protected override void GetUnityState(ref UnityRigidbodyState state)
        {
            state.isKinematic = isKinematic;
            state.linearVelocity = linearVelocity;
            state.angularVelocity = angularVelocity;
            state.isSleeping = _rigidbody.IsSleeping();
        }

        protected override void SetUnityState(UnityRigidbodyState state)
        {
            isKinematic = state.isKinematic;

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

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            if (_ignoreTriggerOnTrigger && other.isTrigger)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            if (_ignoreTriggerOnTrigger && other.isTrigger)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
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


        public void RaiseTriggerEnter(GameObject other)
        {
            onTriggerEnter?.Invoke(other);
        }

        public void RaiseTriggerExit(GameObject other)
        {
            onTriggerExit?.Invoke(other);
        }

        public void RaiseTriggerStay(GameObject other)
        {
            onTriggerStay?.Invoke(other);
        }

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts)
        {
            onCollisionEnter?.Invoke(other, evContacts);
        }

        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts)
        {
            onCollisionExit?.Invoke(other, evContacts);
        }

        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts)
        {
            onCollisionStay?.Invoke(other, evContacts);
        }
#else
        public void RaiseTriggerEnter(GameObject other)
        {
            throw new NotImplementedException();
        }

        public void RaiseTriggerExit(GameObject other)
        {
            throw new NotImplementedException();
        }

        public void RaiseTriggerStay(GameObject other)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionEnter(GameObject other, PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionExit(GameObject other, PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }

        public void RaiseCollisionStay(GameObject other, PhysicsCollision evContacts)
        {
            throw new NotImplementedException();
        }
#endif
    }
}
