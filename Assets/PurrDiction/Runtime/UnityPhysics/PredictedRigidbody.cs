using System;
using PurrNet.Pooling;
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

    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody")]
    public class PredictedRigidbody : PredictedIdentity<UnityRigidbodyState>
    {
        public delegate void OnCollisionDelegate(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts);
        public delegate void OnTriggerDelegate(PredictedRigidbody other);

        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private PhysicsEventMask _eventMask = (PhysicsEventMask)0x3F;
        public new Rigidbody rigidbody => _rigidbody;

        public Rigidbody rb => _rigidbody;

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        public Vector3 linearVelocity
        {
            get => _rigidbody.linearVelocity;
            set => _rigidbody.linearVelocity = value;
        }

        public Vector3 velocity
        {
            get => _rigidbody.linearVelocity;
            set => _rigidbody.linearVelocity = value;
        }

        public Vector3 angularVelocity
        {
            get => _rigidbody.angularVelocity;
            set => _rigidbody.angularVelocity = value;
        }

        /// <summary>
        ///   <para>Adds a force to the Rigidbody.</para>
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            _rigidbody.linearVelocity += mode switch
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

        protected override UnityRigidbodyState GetInitialState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = _rigidbody.linearVelocity,
                angularVelocity = _rigidbody.angularVelocity
            };
        }

        protected override void GetUnityState(ref UnityRigidbodyState state)
        {
            state.linearVelocity = _rigidbody.linearVelocity;
            state.angularVelocity = _rigidbody.angularVelocity;
        }

        protected override void SetUnityState(UnityRigidbodyState state)
        {
            _rigidbody.linearVelocity = state.linearVelocity;
            _rigidbody.angularVelocity = state.angularVelocity;
        }

        private void OnCollisionEnter(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay(Collision other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerified)
                return;

            predictionManager.physics3d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void RaiseTriggerEnter(PredictedRigidbody other)
        {
            onTriggerEnter?.Invoke(other);
        }

        public void RaiseTriggerExit(PredictedRigidbody other)
        {
            onTriggerExit?.Invoke(other);
        }

        public void RaiseTriggerStay(PredictedRigidbody other)
        {
            onTriggerStay?.Invoke(other);
        }

        public void RaiseCollisionEnter(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionEnter?.Invoke(other, evContacts);
        }

        public void RaiseCollisionExit(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionExit?.Invoke(other, evContacts);
        }

        public void RaiseCollisionStay(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            onCollisionStay?.Invoke(other, evContacts);
        }
    }
}
