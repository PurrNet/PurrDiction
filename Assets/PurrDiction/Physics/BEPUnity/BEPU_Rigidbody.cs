using System;
using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Entities;
using BEPUphysics.Entities.Prefabs;
using BEPUphysics.EntityStateManagement;
using BEPUphysics.PositionUpdating;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;

namespace BEPUphysics.Unity
{
    [DefaultExecutionOrder(-5000)]
    public class BEPU_Rigidbody : MonoBehaviour
    {
        private BEPU_PhysicsSpace _physicsSpace;
        private Space _space;
        private readonly List<CompoundShapeEntry> _entities = new ();
        private readonly List<StaticMesh> _staticEntities = new ();

        [SerializeField] private bool _kinematic;
        [SerializeField] double _mass = 1;
        [SerializeField] PositionUpdateMode _positionUpdateMode = PositionUpdateMode.Discrete;
        [SerializeField] Collider[] _colliders;
        
        public Entity entity => _entity;

        private FPVector3 _offset;
        private Transform _trs;
        private Entity _entity;
        
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

        public FPVector3 velocity
        {
            get => _entity.LinearVelocity;
            set => _entity.LinearVelocity = value;
        }
        
        public FPVector3 angularVelocity
        {
            get => _entity.AngularVelocity;
            set => _entity.AngularVelocity = value;
        }
        
        public bool isKinematic
        {
            get => _kinematic;
            set
            {
                _kinematic = value;
                if (_entity == null)
                    return;
                if (_kinematic)
                    _entity.BecomeKinematic();
                else _entity.BecomeDynamic((FP)_mass);
            }
        }
        
        public MotionState motionState
        {
            get => _entity?.MotionState ?? default;
            set
            {
                if (_entity == null)
                    return;
                _entity.MotionState = value;
            }
        }
        
        private void Awake()
        {
            _trs = transform;
            _physicsSpace = BEPU_PhysicsSpace.GetSpace(gameObject);
            
            if (!_physicsSpace)
                throw new InvalidOperationException("No BEPU_PhysicsSpace found in scene.");

            _space = _physicsSpace.space;
        }

        private void OnDisable()
        {
            _physicsSpace.onPostSimulate -= OnPostSimulate;

            if (_entity != null)
                _space.Remove(_entity);
            
            for (int i = 0; i < _staticEntities.Count; i++)
                _space.Remove(_staticEntities[i]);
        }

        private void OnEnable()
        {
            _physicsSpace.onPostSimulate += OnPostSimulate;

            _entities.Clear();
            _staticEntities.Clear();
            _entity = null;
            
            for (var i = 0; i < _colliders.Length; i++)
            {
                var trs = _colliders[i].transform;
                
                var myPos = trs.position;
                var myRot = trs.rotation;
                var myScale = trs.lossyScale;

                MathConverter.Convert(ref myPos, out var bepuPos);
                MathConverter.Convert(ref myRot, out var bepuRot);
                MathConverter.Convert(ref myScale, out var bepuScale);
                
                switch (_colliders[i])
                {
                    case BoxCollider box: AddBoxCollider(_trs, box, bepuPos, bepuRot); break;
                    case MeshCollider mesh: AddMeshCollider(mesh, bepuPos, bepuRot, bepuScale); break;
                    case SphereCollider sphere:
                        var e = new SphereShape((FP)sphere.radius);
                        _entities.Add(new CompoundShapeEntry(e, new RigidTransform(bepuPos, bepuRot), 1));
                        break;
                    case CapsuleCollider capsule:
                        var entity2 = new CapsuleShape((FP)capsule.radius, (FP)capsule.height);
                        _entities.Add(new CompoundShapeEntry(entity2, new RigidTransform(bepuPos, bepuRot), 1));
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported collider type {_colliders[i].GetType().Name}.");
                }
            }

            if (_entities.Count > 0)
                _entity = new CompoundBody(_entities, (FP)_mass);
            
            if (_entity != null)
            {
                _entity.PositionUpdateMode = _positionUpdateMode;
                _entity.Orientation = MathConverter.Convert(_trs.rotation);

                if (_kinematic)
                    _entity.BecomeKinematic();
                else _entity.BecomeDynamic((FP)_mass);
                
                _space.Add(_entity);
            }

            for (int i = 0; i < _staticEntities.Count; i++)
                _space.Add(_staticEntities[i]);
        }

        private void OnPostSimulate()
        {
            if (_entity == null)
                return;
            
            if (!_kinematic)
            {
                _trs.SetPositionAndRotation(
                    MathConverter.Convert(_entity.Position), 
                    MathConverter.Convert(_entity.Orientation)
                );
            }
            else
            {
                _entity.Position = MathConverter.Convert(_trs.position);
                _entity.Orientation = MathConverter.Convert(_trs.rotation);
            }
        }

        private void AddMeshCollider(MeshCollider meshCollider, FPVector3 colliderWorldPos, FPQuaternion colliderWorldRot, FPVector3 colliderWorldScale)
        {
            var mesh = meshCollider.sharedMesh;

            var vertices = new FPVector3[mesh.vertexCount];
            var unityVerts = mesh.vertices;

            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = MathConverter.Convert(unityVerts[i]);
            
            var affineTransform = new AffineTransform(colliderWorldScale, colliderWorldRot, colliderWorldPos);
            
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var e = new StaticMesh(vertices, mesh.GetIndices(i), affineTransform);
                _staticEntities.Add(e);
            }
        }

        private void AddBoxCollider(Transform trs, BoxCollider box, FPVector3 bepuPos, FPQuaternion bepuRot)
        {
            var worldSize = UnityEngine.Vector3.Scale(trs.lossyScale, box.size);
            
            worldSize.x = Mathf.Abs(worldSize.x);
            worldSize.y = Mathf.Abs(worldSize.y);
            worldSize.z = Mathf.Abs(worldSize.z);
            
            bepuPos += MathConverter.Convert(trs.TransformVector(box.center));
            
            MathConverter.Convert(ref worldSize, out var bepuSize);
            
            var e = new BoxShape(bepuSize.x, bepuSize.y, bepuSize.z);
            _entities.Add(new CompoundShapeEntry(e, new RigidTransform(bepuPos, FPQuaternion.Identity), 1));
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
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        public void AddForce(FPVector3 force, ForceMode mode = ForceMode.Force)
        {
             if (_entity == null)
                return;
                
            var delta = _physicsSpace.timeStep;
            _entity.ActivityInformation.Activate();
                
            switch (mode)
            {
                case ForceMode.Force:
                    // Apply continuous force
                    force *= delta;
                    _entity.ApplyLinearImpulse(ref force);
                    break;

                case ForceMode.Acceleration:
                    // Apply force without considering mass
                    force = force * _entity.Mass * delta;
                    _entity.ApplyLinearImpulse(ref force);
                    break;

                case ForceMode.Impulse:
                    // Apply instant force impulse
                    _entity.ApplyLinearImpulse(ref force);
                    break;

                case ForceMode.VelocityChange:
                    // Apply instant velocity change, ignoring mass
                    _entity.LinearVelocity += force;
                    break;
            }
        }

        /*public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            if (_entity == null)
                return;
            
            _entity.ActivityInformation.Activate();

            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyLinearImpulse(ref force);
                    break;
                case ForceMode.Impulse:
                    force *= (Fix64)Time.fixedDeltaTime;
                    _entity.ApplyLinearImpulse(ref force);
                    break;
                case ForceMode.Acceleration:
                    force *= (Fix64)Time.fixedDeltaTime * (Fix64)_mass;
                    _entity.ApplyLinearImpulse(ref force);
                    break;
                case ForceMode.VelocityChange:
                    _entity.LinearVelocity += force;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }*/
        
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
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
    }
}
