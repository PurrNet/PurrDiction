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
    [RequireComponent(typeof(PredictedTransform))]
    public class BepuRigidbody : PredictedIdentity<BepuRigidbody.BepuRigidbodyState>
    {
        [SerializeField] private BepuColliderDefinition[] _colliders;
        [SerializeField] private bool _isKinematic;
        [SerializeField] private FP _mass = FP.C1;
        
        [SerializeField] private FP _linearDrag = FP.C0;
        [SerializeField] private FP _angularDrag = FP.C0p15;
        
        [Header("Constraints")]
        [SerializeField] private bool _freezePositionX;
        [SerializeField] private bool _freezePositionY;
        [SerializeField] private bool _freezePositionZ;
        [SerializeField] private bool _freezeRotationX;
        [SerializeField] private bool _freezeRotationY;
        [SerializeField] private bool _freezeRotationZ;
        
        private Entity _entity;
        private BEPUphysics.Space _space;
        private FPVector3 _lockedPosition;
        private FPQuaternion _lockedRotation;
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
        
        public FP drag
        {
            get => _linearDrag;
            set => _linearDrag = FP.Max(FP.C0, value);
        }
    
        public FP angularDrag
        {
            get => _angularDrag;
            set => _angularDrag = FP.Max(FP.C0, value);
        }

        #region Constraints

        public bool freezePositionX
        {
            get => _freezePositionX;
            set
            {
                _freezePositionX = value;
                if (value) _lockedPosition.x = _entity?.Position.x ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezePositionY
        {
            get => _freezePositionY;
            set
            {
                _freezePositionY = value;
                if (value) _lockedPosition.y = _entity?.Position.y ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezePositionZ
        {
            get => _freezePositionZ;
            set
            {
                _freezePositionZ = value;
                if (value) _lockedPosition.z = _entity?.Position.z ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezeRotationX
        {
            get => _freezeRotationX;
            set
            {
                _freezeRotationX = value;
                if (value) _lockedRotation = _entity?.Orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        public bool freezeRotationY
        {
            get => _freezeRotationY;
            set
            {
                _freezeRotationY = value;
                if (value) _lockedRotation = _entity?.Orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        public bool freezeRotationZ
        {
            get => _freezeRotationZ;
            set
            {
                _freezeRotationZ = value;
                if (value) _lockedRotation = _entity?.Orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        #endregion

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
        
            _lockedPosition = _entity.Position;
            _lockedRotation = _entity.Orientation;
            UpdateConstraints();
        
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

                if (_linearDrag > FP.C0)
                {
                    FP dragFactor = FP.C1 - (delta * _linearDrag);
                    state.linearVelocity *= FP.Max(FP.C0, dragFactor);
                }

                if (_angularDrag > FP.C0)
                {
                    FP angularDragFactor = FP.C1 - (delta * _angularDrag);
                    state.angularVelocity *= FP.Max(FP.C0, angularDragFactor);
                }
            }

            _entity.LinearVelocity = state.linearVelocity;
            _entity.AngularVelocity = state.angularVelocity;

            EnforceConstraints();

            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
        
            transform.SetPositionAndRotation(
                state.position.ToVector3(),
                state.orientation.ToQuaternion());
        }
        
        private void EnforceConstraints()
        {
            if (_entity == null) return;

            var currentPos = _entity.Position;
            var currentRot = _entity.Orientation;

            if (_freezePositionX) currentPos.x = _lockedPosition.x;
            if (_freezePositionY) currentPos.y = _lockedPosition.y;
            if (_freezePositionZ) currentPos.z = _lockedPosition.z;

            if (_freezeRotationX || _freezeRotationY || _freezeRotationZ)
            {
                FPVector3 eulerAngles = currentRot.ToEuler();
                FPVector3 lockedEulerAngles = _lockedRotation.ToEuler();

                if (_freezeRotationX) eulerAngles.x = lockedEulerAngles.x;
                if (_freezeRotationY) eulerAngles.y = lockedEulerAngles.y;
                if (_freezeRotationZ) eulerAngles.z = lockedEulerAngles.z;

                currentRot = FPQuaternion.CreateFromEuler(eulerAngles);
            }

            _entity.Position = currentPos;
            _entity.Orientation = currentRot;

            var linearVel = _entity.LinearVelocity;
            var angularVel = _entity.AngularVelocity;

            if (_freezePositionX) linearVel.x = FP.C0;
            if (_freezePositionY) linearVel.y = FP.C0;
            if (_freezePositionZ) linearVel.z = FP.C0;

            if (_freezeRotationX) angularVel.x = FP.C0;
            if (_freezeRotationY) angularVel.y = FP.C0;
            if (_freezeRotationZ) angularVel.z = FP.C0;

            _entity.LinearVelocity = linearVel;
            _entity.AngularVelocity = angularVel;
        }
        
        private void UpdateConstraints()
        {
            if (_entity == null)
                return;

            _lockedPosition = _entity.Position;
            _lockedRotation = _entity.Orientation;

            EnforceConstraints();
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
