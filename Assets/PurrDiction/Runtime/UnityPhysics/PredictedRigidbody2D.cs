using System;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PredictedTransform))]
    [AddComponentMenu("PurrDiction/Unity Rigidbody/Predicted Rigidbody 2D")]
    public class PredictedRigidbody2D : PredictedIdentity<UnityRigidbody2DState>
    {
        public delegate void OnCollisionDelegate(GameObject other, DisposableList<Physics2DContactPoint> evContacts);
        public delegate void OnTriggerDelegate(GameObject other);

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

        protected override void LateAwake()
        {
            if (predictionManager.physics2d == null)
                _eventMask = PhysicsEventMask.None;
        }


        protected override UnityRigidbody2DState GetInitialState()
        {
            return new UnityRigidbody2DState
            {
                linearVelocity = _rigidbody.linearVelocity,
                angularVelocity = _rigidbody.angularVelocity,
                linearDamping = _rigidbody.linearDamping
            };
        }

        protected override void GetUnityState(ref UnityRigidbody2DState state)
        {
            state.linearVelocity = _rigidbody.linearVelocity;
            state.angularVelocity = _rigidbody.angularVelocity;
            state.linearDamping = _rigidbody.linearDamping;
        }

        protected override void SetUnityState(UnityRigidbody2DState state)
        {
            _rigidbody.linearVelocity = state.linearVelocity;
            _rigidbody.angularVelocity = state.angularVelocity;
            _rigidbody.linearDamping = state.linearDamping;
        }

        private void OnCollisionEnter2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnCollisionExit2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnCollisionStay2D(Collision2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.CollisionStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerEnter))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Enter, this, other);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerExit))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Exit, this, other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            if (!_eventMask.HasFlag(PhysicsEventMask.TriggerStay))
                return;

            if (!predictionManager.isSimulating || predictionManager.isVerifiedAndReplaying)
                return;

            predictionManager.physics2d.RegisterEvent(PhysicsEventType.Stay, this, other);
        }

        public void RaiseTriggerEnter(GameObject other) => onTriggerEnter?.Invoke(other);

        public void RaiseTriggerExit(GameObject other) => onTriggerExit?.Invoke(other);

        public void RaiseTriggerStay(GameObject other) => onTriggerStay?.Invoke(other);

        public void RaiseCollisionEnter(GameObject other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionEnter?.Invoke(other, evContacts);
        }

        public void RaiseCollisionExit(GameObject other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionExit?.Invoke(other, evContacts);
        }

        public void RaiseCollisionStay(GameObject other, DisposableList<Physics2DContactPoint> evContacts)
        {
            onCollisionStay?.Invoke(other, evContacts);
        }

        public Vector2 linearVelocity
        {
            get => _rigidbody.linearVelocity;
            set => _rigidbody.linearVelocity = value;
        }

        public Vector2 velocity
        {
            get => _rigidbody.linearVelocity;
            set => _rigidbody.linearVelocity = value;
        }

        public float angularVelocity
        {
            get => _rigidbody.angularVelocity;
            set => _rigidbody.angularVelocity = value;
        }

        /// <summary>
        /// Adds a force to the Rigidbody2D.
        /// </summary>
        /// <param name="force">Force vector in world coordinates.</param>
        /// <param name="mode">Type of force to apply.</param>
        public void AddForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
        {
            _rigidbody.linearVelocity += mode switch
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
            _rigidbody.angularVelocity += mode switch
            {
                ForceMode2D.Force => torque / _rigidbody.mass * predictionManager.tickDelta,
                ForceMode2D.Impulse => torque / _rigidbody.mass,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
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
            // In 2D, torque is always around the Z axis, so relative torque is the same as world torque
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
            // Apply linear force
            AddForce(force, mode);

            // Calculate and apply torque
            Vector2 relativePosition = position - _rigidbody.worldCenterOfMass;
            float torque = Vector2.SignedAngle(Vector2.right, relativePosition) * force.magnitude;
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

            // Normalize without division by zero
            Vector2 direction = distance > 0.01f ? explosionToObject / distance : Vector2.up;

            // Calculate force based on distance
            float force = explosionForce * (1.0f - Mathf.Clamp01(distance / explosionRadius));

            // Apply force
            AddForceAtPosition(direction * force, _rigidbody.position, mode);
        }
    }
}
