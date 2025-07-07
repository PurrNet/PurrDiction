using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private GameObject _projectile;
        [SerializeField] private Rigidbody _controller;
        [SerializeField] private float _speed = 5;

        protected override void SanitizeInput(ref SimpleWASDInput input)
        {
            var move = new Vector2(input.horizontal, input.vertical);
            move = Vector2.ClampMagnitude(move, 1);

            input.horizontal = move.x;
            input.vertical = move.y;
        }

        protected override void ModifyExtrapolatedInput(ref SimpleWASDInput input)
        {
            input.jump = false;
            input.dash = false;
        }

        protected override void Simulate(SimpleWASDInput input, ref SimpleCCState state, float delta)
        {
            var move = new Vector3(input.horizontal, 0, input.vertical);
            var moveVector = move * _speed;

            if (move != Vector3.zero)
                state.rotation = Mathf.Atan2(move.x, move.z) * Mathf.Rad2Deg;

            _controller.rotation = Quaternion.Euler(0, state.rotation, 0);

            var vel = _controller.linearVelocity;
            vel.x = moveVector.x;
            vel.z = moveVector.z;
            _controller.linearVelocity = vel;

            if (input.jump)
                Shoot();
        }

        private void Shoot()
        {
            var pos = transform.position + transform.forward;
            var projectileId = hierarchy.Create(_projectile, pos, transform.rotation);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
            projectileRb.linearVelocity = transform.forward * 10;
        }

        protected override void GetFinalInput(ref SimpleWASDInput input)
        {
            input.horizontal = Input.GetAxisRaw("Horizontal");
            input.vertical = Input.GetAxisRaw("Vertical");
            input.dash = Input.GetKey(KeyCode.LeftShift);
        }

        protected override void UpdateInput(ref SimpleWASDInput input)
        {
            input.jump |= Input.GetKeyDown(KeyCode.Space);
        }
    }
}
