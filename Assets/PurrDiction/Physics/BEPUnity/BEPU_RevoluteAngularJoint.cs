using System;
using BEPUphysics.Constraints.TwoEntity.Joints;
using ConversionHelper;
using UnityEngine;

namespace BEPUphysics.Unity
{
    [DefaultExecutionOrder(-3000)]
    public class BEPU_RevoluteAngularJoint : MonoBehaviour
    {
        private BEPU_PhysicsSpace _physicsSpace;

        [SerializeField] private BEPU_Rigidbody _bodyA;
        [SerializeField] private BEPU_Rigidbody _bodyB;
        [SerializeField] private Vector3 _axis;
        
        RevoluteAngularJoint _joint;

        private void Awake()
        {
            _physicsSpace = BEPU_PhysicsSpace.GetSpace(gameObject);
            
            if (_physicsSpace == null)
                throw new Exception("BEPU_RotationalConstraint must be in a scene with a BEPU_PhysicsSpace.");
        }

        private void OnEnable()
        {
            var a = _bodyA ? _bodyA.entity : null;
            var b = _bodyB ? _bodyB.entity : null;
            
            _joint = new RevoluteAngularJoint(a, b, MathConverter.Convert(_axis));
            _physicsSpace.space.Add(_joint);
        }
        
        private void OnDisable()
        {
            if (_joint != null)
                _physicsSpace.space.Remove(_joint);
        }
    }
}
