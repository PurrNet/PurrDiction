using System;
using BEPUphysics.Entities;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/BEPU/Bepu Rigidbody")]
    public class BepuRigidbody : PredictedIdentity<BepuRigidbody.BepuRigidbodyState>
    {
        [Header("Bepu Rigidbody")]
        [SerializeField] private BepuColliderDefinition[] _colliders;
        [SerializeField] private bool _isKinematic;
        [SerializeField] private FP _mass = FP.C1;
        
        private Entity _entity;
        private BEPUphysics.Space _space;
        public Action onEntityCreated;
        
        public Entity entity => _entity;
        
        public FPVector3 position
        {
            get => _entity.Position;
            set => _entity.Position = value;
        }
        
        public FPQuaternion rotation
        {
            get => _entity.Orientation;
            set => _entity.Orientation = value;
        }
        
        public bool isKinematic
        {
            get => _isKinematic;
            set
            {
                _isKinematic = value;
                if (_entity == null)
                    return;
                if (_isKinematic)
                    _entity.BecomeKinematic();
                else _entity.BecomeDynamic((FP)_mass);
            }
        }

        public FPVector3 linearVelocity
        {
            get
            {
                return currentState.linearVelocity;
            }
            set
            {
                _entity.LinearVelocity = value;
            }
        }
        
        public FPVector3 angularVelocity => currentState.angularVelocity;
        
        public FP mass => _mass;

        public override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn)
                return;
            
            _space = world.physics;
            
            if (_space == null)
            {
                PurrLogger.LogException($"To use BepuRigidbody you need to select <b>BEPUPhysics</b> as a provider in the PredictionManager.", this);
                base.Setup(manager, world, id);
                return;
            }
            
            CreateEntity();
            base.Setup(manager, world, id);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            
            if (_space != null && _entity != null)
                _space.Remove(_entity);
        }

        private void CreateEntity()
        {
            _entity = BepuColliderFactory.Create(transform, _colliders, mass);
            
            if (_isKinematic)
                _entity.BecomeKinematic();
            
            _space.Add(_entity);
            onEntityCreated?.Invoke();
        }

        protected override BepuRigidbodyState GetInitialState()
        {
            return new BepuRigidbodyState
            {
                position = transform.position.ToFPVector3(),
                orientation = transform.rotation.ToFPQuaternion(),
                linearVelocity = default,
                angularVelocity = default
            };
        }

        protected override void Simulate(ref BepuRigidbodyState state, FP delta)
        {
            if (_space == null)
                return;
            
            if (!_isKinematic)
            {
                state.linearVelocity += _space.ForceUpdater.Gravity * delta;
            }

            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
            
            transform.SetPositionAndRotation(
                state.position.ToVector3(),
                state.orientation.ToQuaternion());
        }

        protected override void GetUnityState(ref BepuRigidbodyState state)
        {
            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
        }

        protected override void SetUnityState(BepuRigidbodyState state)
        {
            _entity.Position = state.position;
            _entity.Orientation = state.orientation;
            _entity.LinearVelocity = state.linearVelocity;
            _entity.AngularVelocity = state.angularVelocity;
        }

        public void AddForce(FPVector3 force, ForceMode mode = ForceMode.Force)
        {
            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyLinearImpulse(force * predictionManager.tickDelta);
                    break;
                case ForceMode.Impulse:
                    _entity.ApplyLinearImpulse(force);
                    break;
                case ForceMode.Acceleration:
                    _entity.ApplyLinearImpulse(force * mass * predictionManager.tickDelta);
                    break;
                case ForceMode.VelocityChange:
                    _entity.LinearVelocity += force;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }
        
        public void AddTorque(FPVector3 torque, ForceMode mode = ForceMode.Force)
        {
            if (_entity == null)
                return;
            
            _entity.ActivityInformation.Activate();

            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.Impulse:
                    torque *= (FP)Time.fixedDeltaTime;
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.Acceleration:
                    torque *= (FP)Time.fixedDeltaTime * (FP)_mass;
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.VelocityChange:
                    _entity.AngularVelocity += torque;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }
        
        public void AddForceAtPosition(FPVector3 force, FPVector3 pos, ForceMode mode = ForceMode.Force)
        {
            if (_entity == null)
                return;
            
            _entity.ActivityInformation.Activate();

            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.Impulse:
                    force *= (FP)Time.fixedDeltaTime;
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.Acceleration:
                    force *= (FP)Time.fixedDeltaTime * (FP)_mass;
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.VelocityChange:
                    _entity.LinearVelocity += force;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }
        
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_colliders == null) 
                return;

            BepuColliderFactory.DrawGizmos(transform, _colliders);
        }
#endif
        
        public struct BepuRigidbodyState : IPredictedData<BepuRigidbodyState>
        {
            public FPVector3 position;
            public FPQuaternion orientation;
            public FPVector3 linearVelocity;
            public FPVector3 angularVelocity;
        }
    }
}
