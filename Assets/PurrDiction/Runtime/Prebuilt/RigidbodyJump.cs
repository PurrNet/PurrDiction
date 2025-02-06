using System;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Prebuilt
{
    [RequireComponent(typeof(PredictedRigidbody))]
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Jump")]
    public class RigidbodyJump : PredictedIdentity<RigidbodyJump.JumpInput, RigidbodyJump.JumpData>
    {
        [SerializeField] private KeyCode jumpKey = KeyCode.Space;
        [SerializeField] private Rigidbody rigidbody;
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
        
        private void Reset()
        {
            if(!TryGetComponent(out rigidbody))
                rigidbody = gameObject.AddComponent<Rigidbody>();
        }

        protected override void Simulate(JumpInput? input, ref JumpData state, FP delta)
        {
            if (!input.HasValue)
                return;

            bool isGrounded = IsGrounded();

            if (!isGrounded)
            {
                state.timeInAir += delta;
                if(rigidbody.linearVelocity.magnitude < maxFallSpeed)
                    rigidbody.AddForce(Vector3.down * ((float)state.timeInAir * gravityAirTimeMultiplier), ForceMode.Acceleration);
            }
            else
            {
                state.timeInAir = 0;
            }
            
            state.timeSinceJump += delta;

            if (input.Value.jump && state.timeSinceJump >= (FP)jumpCooldown && isGrounded)
            {
                state.timeSinceJump = 0;
                switch (upOrientation)
                {
                    case UpDirection.World:
                        rigidbody.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
                        break;
                    case UpDirection.Local:
                        rigidbody.AddForce(transform.up * jumpForce, ForceMode.Impulse);
                        break;
                }
            }
        }
        
        private static readonly Collider[] GroundCheckResults = new Collider[5];        
        private bool IsGrounded()
        {
            var hits = Physics.OverlapSphereNonAlloc(transform.TransformPoint(groundCheckOffset), groundCheckRadius, GroundCheckResults, groundLayer);
            for (int i = 0; i < hits; i++)
            {
                if(GroundCheckResults[i].gameObject != gameObject)
                    return true;
            }
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
        

        protected override JumpInput GetInput()
        {
            var input = new JumpInput()
            {
                jump = Input.GetKey(jumpKey)
            };
            return input;
        }
        
        public struct JumpData : IPredictedData<JumpData>
        {
            public FP timeInAir;
            public FP timeSinceJump;
        }

        public struct JumpInput : IPredictedData
        {
            public bool jump;
        }

        private enum UpDirection
        {
            World,
            Local
        }
    }
}
