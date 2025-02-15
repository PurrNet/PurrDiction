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
        Vector3 position = transform.position;
        Vector3 normalizedAxis = _axis.normalized;
        Vector3 testAxis;

        if (Application.isPlaying)
        {
            if (!_initialized)
            {
                Vector3 toConnected = (connectedBody.transform.position - position).normalized;
                _initialTestAxis = Vector3.Cross(normalizedAxis, toConnected).normalized;
                _initialTestAxis = Quaternion.AngleAxis(-90, normalizedAxis) * _initialTestAxis;
                _initialized = true;
            }
            testAxis = _initialTestAxis;
        }
        else
        {
            if (connectedBody != null)
            {
                Vector3 toConnected = (connectedBody.transform.position - position).normalized;
                testAxis = Vector3.Cross(normalizedAxis, toConnected).normalized;
                testAxis = Quaternion.AngleAxis(-90, normalizedAxis) * testAxis;
            }
            else
            {
                testAxis = (Mathf.Abs(Vector3.Dot(normalizedAxis, Vector3.up)) > 0.9f ? 
                    Vector3.right : Vector3.Cross(Vector3.up, normalizedAxis)).normalized;
            }
        }
        
        Gizmos.color = Color.blue;
        Gizmos.DrawRay(position, normalizedAxis);
        
        Gizmos.color = Color.yellow;
        var minAngle = -(angleLimitation / 2);
        var maxAngle = angleLimitation / 2;
        Quaternion minRot = Quaternion.AngleAxis((float)minAngle, normalizedAxis);
        Quaternion maxRot = Quaternion.AngleAxis((float)maxAngle, normalizedAxis);
        
        Gizmos.DrawRay(position, minRot * testAxis);
        Gizmos.DrawRay(position, maxRot * testAxis);
        
        int segments = 20;
        float angleStep = ((float)maxAngle - (float)minAngle) / segments;
        Vector3 prev = position + minRot * testAxis;
        
        for(int i = 1; i <= segments; i++)
        {
            float angle = (float)minAngle + (angleStep * i);
            Vector3 next = position + (Quaternion.AngleAxis(angle, normalizedAxis) * testAxis);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }

        if (connectedBody != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(position, new Vector3(connectedBody.transform.position.x, position.y,  connectedBody.transform.position.z));
        }
    }
#endif

        public struct BepuHingeJointState : IPredictedData<BepuHingeJointState> { }
    }
}
