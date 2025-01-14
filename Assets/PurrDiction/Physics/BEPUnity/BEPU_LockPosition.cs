using System;
using BEPUphysics.Constraints.SingleEntity;
using ConversionHelper;
using UnityEngine;

namespace BEPUphysics.Unity
{
    public class BEPU_LockPosition : MonoBehaviour
    {
        private BEPU_PhysicsSpace _physicsSpace;

        [SerializeField] private BEPU_Rigidbody _body;
        
        SingleEntityLinearMotor _motor;

        private void Reset()
        {
            _body = GetComponent<BEPU_Rigidbody>();
        }

        private void Awake()
        {
            _physicsSpace = BEPU_PhysicsSpace.GetSpace(gameObject);
            
            if (_physicsSpace == null)
                throw new Exception("BEPU_RotationalConstraint must be in a scene with a BEPU_PhysicsSpace.");
        }

        private void OnEnable()
        {
            var body = _body ? _body.entity : null;

            _motor = new SingleEntityLinearMotor(body, MathConverter.Convert(transform.position));
            _physicsSpace.space.Add(_motor);
        }
        
        private void OnDisable()
        {
            if (_motor != null)
                _physicsSpace.space.Remove(_motor);
        }
    }
}
