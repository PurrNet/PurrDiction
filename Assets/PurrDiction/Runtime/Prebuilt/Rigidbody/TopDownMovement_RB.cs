using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Top Down Movement")]
    public class TopDownMovement_RB : PredictedIdentity<TopDownMovement_RB.Input, TopDownMovement_RB.State>
    {
        [SerializeField] private Rigidbody rigidbody;
        [SerializeField] private float maxSpeed = 5;
        [SerializeField] private float acceleration = 30;
        private Camera _camera;

        private void Awake()
        {
            _camera = Camera.main;
            if (!_camera)
                Debug.LogError($"Failed to get camera tagget as main camera!", this);
        }

        private void Reset()
        {
            if(!TryGetComponent(out rigidbody))
                rigidbody = gameObject.AddComponent<Rigidbody>();

            rigidbody.linearDamping = 3;
            rigidbody.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
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
            var movement = input.Value.moveDirection;
            movement.Normalize();
            var floatMovement = MathConverter.Convert(movement);
            
            rigidbody.AddForce(floatMovement * acceleration);
            
            var flatMovement = new Vector3(rigidbody.linearVelocity.x, 0, rigidbody.linearVelocity.z);
            if (flatMovement.magnitude > maxSpeed)
            {
                flatMovement = flatMovement.normalized * maxSpeed;
                flatMovement.y = rigidbody.linearVelocity.y;
                rigidbody.linearVelocity = flatMovement;
            }

            if (floatMovement != Vector3.zero)
            {
                var rotation = Mathf.Atan2(floatMovement.x, floatMovement.z) * Mathf.Rad2Deg;
                state.rotation = (FP)rotation;
            }
            
            rigidbody.rotation = Quaternion.Euler(0, (float)state.rotation, 0);
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
