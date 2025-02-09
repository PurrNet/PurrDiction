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
        public FPVector3 pendingAccelerationForce;
        public FPVector3 pendingImpulseForce;
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
            var entities = new CompoundShapeEntry[_colliders.Length];
            
            for (int i = 0; i < _colliders.Length; i++)
            {
                var collider = _colliders[i];
                EntityShape shape = collider.type switch
                {
                    BepuColliderType.Sphere => new SphereShape(collider.radius),
                    BepuColliderType.Box => new BoxShape(collider.width, collider.height, collider.depth),
                    BepuColliderType.Capsule => new CapsuleShape(collider.radius, collider.height),
                    _ => throw new ArgumentOutOfRangeException()
                };
                
                entities[i] = new CompoundShapeEntry(shape, new RigidTransform(transform.position.ToFPVector3(), transform.rotation.ToFPQuaternion()), F64.C1);
            }

            _entity = new CompoundBody(entities, _mass);
            
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
                angularVelocity = default,
                pendingAccelerationForce = default,
                pendingImpulseForce = default
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

            if (state.pendingAccelerationForce != FPVector3.zero)
            {
                var force = state.pendingAccelerationForce * delta;
                _entity.ApplyLinearImpulse(ref force);
                state.pendingAccelerationForce = FPVector3.zero;
            }

            if (state.pendingImpulseForce != FPVector3.zero)
            {
                _entity.ApplyLinearImpulse(ref state.pendingImpulseForce);
                state.pendingImpulseForce = FPVector3.zero;
            }

            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
            
            transform.position = state.position.ToVector3();
            transform.rotation = state.orientation.ToQuaternion();
        }

        public void AddForce(FPVector3 force, ForceMode mode = ForceMode.Force)
        {
            var state = currentState;
            
            switch (mode)
            {
                case ForceMode.Force:
                    state.pendingAccelerationForce += force;
                    break;
                case ForceMode.Impulse:
                    state.pendingImpulseForce += force;
                    break;
                case ForceMode.Acceleration:
                    state.pendingAccelerationForce += force * _mass;
                    break;
                case ForceMode.VelocityChange:
                    state.linearVelocity += force;
                    break;
            }
            
            currentState = state;
        }
        
        #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_colliders == null) 
                return;

            Gizmos.color = Color.green;
            var position = transform.position;
            var rotation = transform.rotation;

            foreach (var collider in _colliders)
            {
                switch (collider.type)
                {
                    case BepuColliderType.Sphere:
                        Gizmos.DrawWireSphere(position, (float)collider.radius);
                        break;
                    
                    case BepuColliderType.Box:
                        Gizmos.matrix = Matrix4x4.TRS(position, rotation, Vector3.one);
                        Gizmos.DrawWireCube(Vector3.zero, 
                            new Vector3((float)collider.width, (float)collider.height, (float)collider.depth));
                        Gizmos.matrix = Matrix4x4.identity;
                        break;
                    
                    case BepuColliderType.Capsule:
                        var pointOffset = Vector3.up * (float)(collider.height * (FP)0.5f - collider.radius);
                        
                        Gizmos.DrawWireSphere(position + rotation * pointOffset, (float)collider.radius);
                        Gizmos.DrawWireSphere(position + rotation * -pointOffset, (float)collider.radius);
                        
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.right * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.right * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.left * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.left * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.forward * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.forward * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.back * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.back * (float)collider.radius));
                        break;
                }
            }
        }
#endif
    }
}
