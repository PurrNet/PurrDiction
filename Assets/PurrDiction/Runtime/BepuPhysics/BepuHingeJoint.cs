using BEPUphysics.Constraints.SolverGroups;
using BEPUphysics.Constraints.TwoEntity.JointLimits;
using ConversionHelper;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    [RequireComponent(typeof(BepuRigidbody))]
    public class BepuHingeJoint : PredictedIdentity<BepuHingeJoint.BepuHingeJointState>
    {
        [SerializeField] private BepuRigidbody connectedBody;
        [SerializeField] private Vector3 _axis = Vector3.up;
        [SerializeField] private FP angleLimitation = 180;
        
        private RevoluteJoint _hingeJoint;
        private RevoluteLimit _limitJoint;
        private BEPUphysics.Space _space;

        public override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn || !connectedBody)
                return;

            if (!TryGetComponent(out BepuRigidbody self))
                return;

            if (self.entity == null)
            {
                self.onEntityCreated += () => {Setup(manager, world, id);};
                return;
            }

            if (connectedBody.entity == null)
            {
                connectedBody.onEntityCreated += () => {Setup(manager, world, id);};
                return;
            }

            if (!self.entity.IsDynamic && !connectedBody.entity.IsDynamic)
            {
                PurrLogger.LogError($"Can't create a hinge joint between two kinematic bodies!", this);
                return;
            }
            
            _space = world.physics;
            
            if (_space == null)
            {
                PurrLogger.LogException($"To use BepuRigidbody you need to select <b>BEPUPhysics</b> as a provider in the PredictionManager.", this);
                base.Setup(manager, world, id);
                return;
            }
            
            var a = connectedBody ? connectedBody.entity : null;
            var b = self.entity;

            Vector3 normalizedAxis = _axis.normalized;
            Vector3 testAxis = (Mathf.Abs(Vector3.Dot(normalizedAxis, Vector3.up)) > 0.9f ? 
                Vector3.right : Vector3.Cross(Vector3.up, normalizedAxis)).normalized;
            
            FP min = (FP)((float)-(angleLimitation / 2) * Mathf.Deg2Rad);
            FP max = (FP)((float)angleLimitation / 2 * Mathf.Deg2Rad);
            
            _hingeJoint = new RevoluteJoint(a, b, b.Position, normalizedAxis.ToFPVector3());
            _limitJoint = new RevoluteLimit(a, b, normalizedAxis.ToFPVector3(), testAxis.ToFPVector3(), min, max);
            
            _space.Add(_hingeJoint);
            _space.Add(_limitJoint);
            
            base.Setup(manager, world, id);
        }

        protected override void Simulate(ref BepuHingeJointState state, FP delta)
        {
            base.Simulate(ref state, delta);
            if(_hingeJoint != null) _hingeJoint.Update(delta);
            if(_limitJoint != null) _limitJoint.Update(delta);
        }

        private void OnDisable()
        {
            if (_hingeJoint != null && _space != null) 
            { 
                _space.Remove(_hingeJoint); 
                _hingeJoint = null; 
            }
            
            if (_limitJoint != null && _space != null) 
            { 
                _space.Remove(_limitJoint); 
                _limitJoint = null; 
            }
        }

#if UNITY_EDITOR
        private Vector3 _initialTestAxis;
        private bool _initialized;
        
        private void OnDrawGizmosSelected()
        {
            Vector3 testAxis;
            if (Application.isPlaying)
            {
                if (!_initialized && connectedBody != null)
                {
                    Vector3 toConnected = (connectedBody.transform.position - transform.position).normalized;
                    _initialTestAxis = Vector3.Cross(_axis.normalized, toConnected).normalized;
                    _initialTestAxis = Quaternion.AngleAxis(-90, _axis.normalized) * _initialTestAxis;
                    _initialized = true;
                }
                testAxis = _initialTestAxis;
            }
            else
            {
                testAxis = (Mathf.Abs(Vector3.Dot(_axis.normalized, Vector3.up)) > 0.9f ? 
                    Vector3.right : Vector3.Cross(Vector3.up, _axis.normalized)).normalized;
            }

            BepuDebugger.DrawHingeJoint(
                transform.position,
                _axis,
                testAxis,
                (float)angleLimitation,
                connectedBody ? connectedBody.transform : null
            );
        }
#endif

        public struct BepuHingeJointState : IPredictedData<BepuHingeJointState> { }
    }
}
