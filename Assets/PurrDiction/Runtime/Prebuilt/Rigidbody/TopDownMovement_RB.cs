using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Top Down Movement")]
    public class TopDownMovement_RB : PredictedIdentity<TopDownMovement_RB.Input, TopDownMovement_RB.State>
    {
        [FormerlySerializedAs("rigidbody")]
        [SerializeField] private Rigidbody _rigidbody;
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
            if(!TryGetComponent(out _rigidbody))
                _rigidbody = gameObject.AddComponent<Rigidbody>();

            _rigidbody.linearDamping = 3;

            // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
            _rigidbody.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        protected override void GetFinalInput(ref Input input)
        {
            input = new Input()
            {
                moveDirection = GetCameraRelativeMovement(GetMovementInput())
            };
        }

        protected override void Simulate(Input input, ref State state, float delta)
        {
            var movement = input.moveDirection;
            movement.Normalize();
            var floatMovement = movement;

            _rigidbody.AddForce(floatMovement * acceleration);

            var flatMovement = new Vector3(_rigidbody.linearVelocity.x, 0, _rigidbody.linearVelocity.z);
            if (flatMovement.magnitude > maxSpeed)
            {
                flatMovement = flatMovement.normalized * maxSpeed;
                flatMovement.y = _rigidbody.linearVelocity.y;
                _rigidbody.linearVelocity = flatMovement;
            }

            if (floatMovement != Vector3.zero)
            {
                var rotation = Mathf.Atan2(floatMovement.x, floatMovement.z) * Mathf.Rad2Deg;
                state.rotation = rotation;
            }

            _rigidbody.rotation = Quaternion.Euler(0, state.rotation, 0);
        }

        private Vector3 GetCameraRelativeMovement(Vector2 inputDirection)
        {
            if (inputDirection.sqrMagnitude == 0) return Vector3.zero;

            var cameraForward = _camera.transform.forward;
            var cameraRight = _camera.transform.right;

            cameraForward.y = 0;
            cameraRight.y = 0;

            cameraForward.Normalize();
            cameraRight.Normalize();

            var moveDirection = cameraRight * inputDirection.x + cameraForward * inputDirection.y;

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
            public float rotation;

            public void Dispose() { }
        }

        public struct Input : IPredictedData
        {
            public Vector3 moveDirection;

            public void Dispose() { }
        }
    }
}
