using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace PurrNet.Prediction.Prebuilt
{
    [AddComponentMenu("PurrDiction/Prebuilt/BEPU/Top Down Movement")]
    [RequireComponent(typeof(BepuRigidbody))]
    public class TopDownMovement_Bepu : PredictedIdentity<TopDownMovement_Bepu.Input, TopDownMovement_Bepu.State>
    {
        [FormerlySerializedAs("rigidbody")] 
        [SerializeField] private BepuRigidbody _rigidbody;
        [FormerlySerializedAs("maxSpeed")] 
        [SerializeField] private FP _maxSpeed = 5;
        [FormerlySerializedAs("acceleration")] 
        [SerializeField] private FP _acceleration = 30;
        
        private Camera _camera;
        
        private void Awake()
        {
            _camera = Camera.main;
            if (!_camera)
                Debug.LogError($"Failed to get camera tagget as main camera!", this);
        }
        
        private void Reset()
        {
            if(!TryGetComponent(out _rigidbody))
                _rigidbody = gameObject.AddComponent<BepuRigidbody>();

        }

        protected override Input GetInput()
        {
            var input = new Input()
            {
                moveDirection = GetCameraRelativeMovement(GetMovementInput())
            };

            return input;
        }

        protected override void Simulate(Input? input, ref State state, FP delta)
        {
            if (!input.HasValue)
                return;
            
            input.Value.moveDirection.Normalize();
            
            _rigidbody.AddForce(input.Value.moveDirection * _acceleration);
            var flatVelocity = new FPVector2(_rigidbody.linearVelocity.x, _rigidbody.linearVelocity.z);
            if (flatVelocity.magnitude > _maxSpeed)
            {
                flatVelocity = flatVelocity.normalized * _maxSpeed;
                _rigidbody.linearVelocity = new FPVector3(flatVelocity.x, _rigidbody.linearVelocity.y, flatVelocity.y);
            }
        }
        
        private FPVector3 GetCameraRelativeMovement(Vector2 inputDirection)
        {
            if (inputDirection.sqrMagnitude == 0) return FPVector3.zero;
    
            Vector3 cameraForward = _camera.transform.forward;
            Vector3 cameraRight = _camera.transform.right;
    
            cameraForward.y = 0;
            cameraRight.y = 0;
    
            cameraForward.Normalize();
            cameraRight.Normalize();
            
            FPVector3 moveDirection = MathConverter.Convert(cameraRight * inputDirection.x + cameraForward * inputDirection.y);
    
            return moveDirection;
        }

        private Vector2 GetMovementInput()
        {
            var vector = new Vector2();
            if (Keyboard.current != null)
            {
                vector.x = Keyboard.current.aKey.isPressed ? -1 : 0;
                vector.x += Keyboard.current.dKey.isPressed ? 1 : 0;
                vector.y = Keyboard.current.sKey.isPressed ? -1 : 0;
                vector.y += Keyboard.current.wKey.isPressed ? 1 : 0;
            }

            return vector;
        }
        
        public struct State : IPredictedData<State>
        {
            public FP rotation;
        }
        
        public struct Input : IPredictedData
        {
            public FPVector3 moveDirection;
        }
    }
}
