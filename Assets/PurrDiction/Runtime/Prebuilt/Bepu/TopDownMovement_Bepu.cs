using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace PurrNet.Prediction.Prebuilt
{
    [AddComponentMenu("PurrDiction/Prebuilt/BEPU/Top Down Movement")]
    [RequireComponent(typeof(BepuRigidbody))]
    public class TopDownMovement_Bepu : PredictedIdentity<TopDownMovement_Bepu.Input, TopDownMovement_Bepu.State>, IBepuCollisionEnter, IBepuTriggerEnter
    {
        [FormerlySerializedAs("rigidbody")]
        [SerializeField] private BepuRigidbody _rigidbody;
        [FormerlySerializedAs("maxSpeed")]
        [SerializeField] private FP _maxSpeed = 5;
        [FormerlySerializedAs("acceleration")]
        [SerializeField] private FP _acceleration = 30;

        private Camera _camera;
        private bool _wantJump;
        private PredictedEvent _jumpEvent;

        private void Awake()
        {
            _camera = Camera.main;
            if (!_camera)
                Debug.LogError($"Failed to get camera tagget as main camera!", this);
        }

        protected override void OnSpawned()
        {
            _jumpEvent = new PredictedEvent(predictionManager, this);
            _jumpEvent.AddListener(OnJumpedVFX);
        }

        protected override void OnDespawned()
        {
            _jumpEvent.RemoveListener(OnJumpedVFX);
        }

        private void OnJumpedVFX()
        {
            PurrLogger.Log("Jumped!");
        }

        private void Update()
        {
            if (!_wantJump)
                _wantJump = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
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

            input.jump = _wantJump;
            _wantJump = false;

            return input;
        }

        protected override void ModifyExtrapolatedInput(ref Input input)
        {
            input.jump = false;
        }

        protected override void Simulate(Input? input, ref State state, FP delta)
        {
            var moveDir = input?.moveDirection ?? FPVector3.zero;
            var currVelocity = _rigidbody.linearVelocity;
            var targetVelocity = moveDir * _maxSpeed;
            var interpolationAmount = FP.Clamp01(_acceleration * delta);
            var nextVelocity = FPVector3.Lerp(currVelocity, targetVelocity, interpolationAmount);

            // don't change the y velocity (jumping, falling, etc)
            nextVelocity.y = currVelocity.y;

            if (input?.jump == true)
            {
                _jumpEvent.Invoke();
                nextVelocity.y = 10;
            }

            _rigidbody.linearVelocity = nextVelocity;
        }

        protected override void SanitizeInput(ref Input input)
        {
            if (input.moveDirection != FPVector3.zero)
                input.moveDirection.Normalize();
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
            public bool jump;

            public override string ToString()
            {
                return $"Move: {moveDirection}, Jump: {jump}";
            }
        }

        public void OnBepuCollisionEnter(BepuCollisionData data)
        {
            // Debug.Log($"Collided with {data.other.name}");
        }

        public void OnBepuTriggerEnter(BepuCollisionData data)
        {
            // Debug.Log($"Triggered with {data.other.name}");
        }
    }
}
