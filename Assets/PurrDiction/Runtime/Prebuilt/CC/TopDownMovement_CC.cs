using UnityEngine;
using UnityEngine.InputSystem;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedTransform))]
#if UNITY_PHYSICS_3D
    [RequireComponent(typeof(CharacterController))]
#endif
    [AddComponentMenu("PurrDiction/Prebuilt/Transform/Top Down Movement")]
    public class TopDownMovement_CC : PredictedIdentity<TopDownMovement_CC.Input, TopDownMovement_CC.State>
    {
#if UNITY_PHYSICS_3D
        [SerializeField] private CharacterController controller;
#endif
        [SerializeField] private float movementSpeed = 5;
        private Camera _camera;

        private void Awake()
        {
            _camera = Camera.main;
            if (!_camera)
                Debug.LogError($"Failed to get camera tagget as main camera!", this);
        }

#if UNITY_PHYSICS_3D
        private void Reset()
        {
            if (!TryGetComponent(out controller))
                controller = gameObject.AddComponent<CharacterController>();
        }
#endif

        protected override void Simulate(Input input, ref State state, float delta)
        {
            var movement = input.moveDirection;
            movement.Normalize();
            var floatMovement = movement;

#if UNITY_PHYSICS_3D
            controller.Move(floatMovement * movementSpeed * delta);
#endif

            if (floatMovement != Vector3.zero)
            {
                var rotation = Mathf.Atan2(floatMovement.x, floatMovement.z) * Mathf.Rad2Deg;
                state.rotation = rotation;
            }

            transform.rotation = Quaternion.Euler(0, state.rotation, 0);
        }

        protected override void GetFinalInput(ref Input input)
        {
            input = new Input()
            {
                moveDirection = GetCameraRelativeMovement(GetMovementInput())
            };
        }

        private Vector3 GetCameraRelativeMovement(Vector2 inputDirection)
        {
            if (inputDirection.sqrMagnitude == 0) return Vector3.zero;

            Vector3 cameraForward = _camera.transform.forward;
            Vector3 cameraRight = _camera.transform.right;

            cameraForward.y = 0;
            cameraRight.y = 0;

            cameraForward.Normalize();
            cameraRight.Normalize();

            Vector3 moveDirection = cameraRight * inputDirection.x + cameraForward * inputDirection.y;

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
