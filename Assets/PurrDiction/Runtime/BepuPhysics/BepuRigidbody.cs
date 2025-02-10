using System;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Entities;
using BEPUphysics.Entities.Prefabs;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct BepuRigidbodyState : IPredictedData<BepuRigidbodyState>
    {
        public FPVector3 position;
        public FPQuaternion orientation;
        public FPVector3 linearVelocity;
        public FPVector3 angularVelocity;
    }
    
    public class BepuRigidbody : PredictedIdentity<BepuRigidbodyState>
    {
        [Header("Bepu Rigidbody")]
        [SerializeField] private BepuColliderDefinition[] _colliders;
        [SerializeField] private bool _isKinematic;
        [SerializeField] private FP _mass = F64.C1;
        
        private Entity _entity;
        private BEPUphysics.Space _space;

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
        
        //public Entity BepuEntity => _entity;

        public override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            base.Setup(manager, world, id);
            
            if (!world)
            {
                PurrLogger.LogException($"Predicted Identity does not have a prediction manager!", this);
                return;
            }
            
            _space = world.physics;
            if (_space == null)
            {
                PurrLogger.LogException($"No physics space found in scene!", this);
                return;
            }
            CreateEntity();
        }

        private void OnDestroy()
        {
            if (_space != null && _entity != null)
                _space.Remove(_entity);
        }

        private void CreateEntity()
        {
            _entity = new CompoundBody(BepuColliderFactory.Create(transform, _colliders), _mass);
            
            if (_isKinematic)
                _entity.BecomeKinematic();
            
            _space.Add(_entity);
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
            
            transform.position = state.position.ToVector3();
            transform.rotation = state.orientation.ToQuaternion();
        }

        protected override void GetUnityState(ref BepuRigidbodyState state)
        {
            if (_entity == null)
                return;
            
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
            var state = currentState;
            
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
            }
            
            currentState = state;
        }
        
        #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_colliders == null) 
                return;

            BepuColliderFactory.DrawGizmos(transform, _colliders);
        }
#endif
    }
}
