using System;
using BEPUphysics.Constraints.TwoEntity.Motors;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;

namespace BEPUphysics.Unity
{
    public class BEPU_RevoluteMotor : MonoBehaviour
    {
        private BEPU_PhysicsSpace _physicsSpace;

        [SerializeField] private BEPU_Rigidbody _bodyA;
        [SerializeField] private BEPU_Rigidbody _bodyB;
        [SerializeField] private Vector3 _axis;
        [Space]
        [SerializeField] private MotorMode _mode;
        [SerializeField] private double _softness = .005d;
        [SerializeField] private double _goalVelocity;
        [SerializeField] private double _goalServo;
        
        RevoluteMotor _motor;

        private void Awake()
        {
            _physicsSpace = BEPU_PhysicsSpace.GetSpace(gameObject);
            
            if (_physicsSpace == null)
                throw new Exception("BEPU_RotationalConstraint must be in a scene with a BEPU_PhysicsSpace.");
        }

        private void OnValidate()
        {
            if (_motor != null)
                UpdateSettings();
        }

        private void UpdateSettings()
        {
            _motor.Settings.Mode = _mode;
            _motor.Settings.VelocityMotor.GoalVelocity = (FP)_goalVelocity;
            _motor.Settings.VelocityMotor.Softness = (FP)_softness;
            _motor.Settings.Servo.Goal = (FP)_goalServo;
        }

        private void OnEnable()
        {
            var a = _bodyA ? _bodyA.entity : null;
            var b = _bodyB ? _bodyB.entity : null;
            
            _motor = new RevoluteMotor(a, b, MathConverter.Convert(_axis));
            UpdateSettings();
            _physicsSpace.space.Add(_motor);
        }
        
        private void OnDisable()
        {
            if (_motor != null)
                _physicsSpace.space.Remove(_motor);
        }
    }
}
