using UnityEngine;
using UnityEngine.Serialization;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Jump")]
    public class RigidbodyJump : PredictedIdentity<RigidbodyJump.JumpInput, RigidbodyJump.JumpData>
    {
        [SerializeField] private KeyCode jumpKey = KeyCode.Space;
#if UNITY_PHYSICS_3D
        [FormerlySerializedAs("rigidbody")]
        [SerializeField] private Rigidbody _rigidbody;
#endif
        [SerializeField] private float jumpCooldown = 0.5f;
        [SerializeField] private float jumpForce = 10;
        [SerializeField] private UpDirection upOrientation;

        [Header("Ground check")]
        [SerializeField] private float groundCheckRadius = 0.25f;
        [SerializeField] private Vector3 groundCheckOffset;
        [SerializeField] private LayerMask groundLayer;

        [Header("Drag settings")] [Tooltip("Will add gravity over time while in air, to counter act the rigidbody drag")]
        [SerializeField] private float gravityAirTimeMultiplier = 25f;
        [Tooltip("Deciphers at what point we stop adding downwards force to the rigidbody")]
        [SerializeField] private float maxFallSpeed = 10;

#if UNITY_EDITOR
        [Header("Debug")]
        [SerializeField] private bool drawGizmos = true;
#endif

#if UNITY_PHYSICS_3D
        private void Reset()
        {
            if(!TryGetComponent(out _rigidbody))
                _rigidbody = gameObject.AddComponent<Rigidbody>();
        }
#endif

        protected override void Simulate(JumpInput input, ref JumpData state, float delta)
        {
            bool isGrounded = IsGrounded();

            if (!isGrounded)
            {
                state.timeInAir += delta;
#if UNITY_PHYSICS_3D
                if(_rigidbody.linearVelocity.magnitude < maxFallSpeed)
                    _rigidbody.AddForce(Vector3.down * (state.timeInAir * gravityAirTimeMultiplier), ForceMode.Acceleration);
#endif
            }
            else
            {
                state.timeInAir = 0;
            }

            state.timeSinceJump += delta;

#if UNITY_PHYSICS_3D
            if (input.jump && state.timeSinceJump >= jumpCooldown && isGrounded)
            {
                state.timeSinceJump = 0;
                switch (upOrientation)
                {
                    case UpDirection.World:
                        _rigidbody.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
                        break;
                    case UpDirection.Local:
                        _rigidbody.AddForce(transform.up * jumpForce, ForceMode.Impulse);
                        break;
                }
            }
#endif
        }

#if UNITY_PHYSICS_3D
        private static readonly Collider[] GroundCheckResults = new Collider[5];
#endif
        private bool IsGrounded()
        {
#if UNITY_PHYSICS_3D
            var hits = Physics.OverlapSphereNonAlloc(transform.TransformPoint(groundCheckOffset), groundCheckRadius, GroundCheckResults, groundLayer);
            for (int i = 0; i < hits; i++)
            {
                if(GroundCheckResults[i].gameObject != gameObject)
                    return true;
            }
#endif
            return false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.TransformPoint(groundCheckOffset), groundCheckRadius);
        }
#endif


        protected override void GetFinalInput(ref JumpInput input)
        {
            input.jump = Input.GetKey(jumpKey);
        }

        public struct JumpData : IPredictedData<JumpData>
        {
            public float timeInAir;
            public float timeSinceJump;

            public void Dispose() { }
        }

        public struct JumpInput : IPredictedData
        {
            public bool jump;

            public void Dispose() { }
        }

        private enum UpDirection
        {
            World,
            Local
        }
    }
}
