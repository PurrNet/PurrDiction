using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private GameObject _projectile;
        [SerializeField] private Rigidbody _controller;
        [SerializeField] private float _speed = 5;

        protected override void SetUnityState(SimpleCCState state)
        {
            _controller.linearVelocity = state.linearVelocity;
            _controller.angularVelocity = state.angularVelocity;
        }

        protected override void GetUnityState(ref SimpleCCState state)
        {
            state.linearVelocity = _controller.linearVelocity;
            state.angularVelocity = _controller.angularVelocity;
        }

        protected override void Simulate(SimpleWASDInput? input, ref SimpleCCState state, Fix64 delta)
        {
            var move = new Vector3(input?.horizontal ?? 0, 0, input?.vertical ?? 0);
            
            if (move.magnitude > 0.01f)
                move.Normalize();
            
            var moveVector = move * _speed;

            if (move != Vector3.zero)
                state.rotation = Mathf.Atan2(move.x, move.z) * Mathf.Rad2Deg;
            
            _controller.rotation = Quaternion.Euler(0, state.rotation, 0);

            var vel = _controller.linearVelocity;
            vel.x = moveVector.x;
            vel.z = moveVector.z;
            _controller.linearVelocity = vel;
            
            if (state.wasShooting != input?.jump)
            {
                state.wasShooting = input?.jump ?? false;
                if (state.wasShooting)
                    Shoot();
            }
        }

        private void Shoot()
        {
            var pos = transform.position + transform.forward;
            
            var projectileId = hierarchy.Create(_projectile, pos, transform.rotation);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
            projectileRb.linearVelocity = transform.forward * 10;
        }

        protected override SimpleWASDInput GetInput()
        {
            return new SimpleWASDInput
            {
                horizontal = Input.GetAxisRaw("Horizontal"),
                vertical = Input.GetAxisRaw("Vertical"),
                jump = Input.GetKey(KeyCode.Space),
                dash = Input.GetKey(KeyCode.LeftShift)
            };
        }
    }
}
