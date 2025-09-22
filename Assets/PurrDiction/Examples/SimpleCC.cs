using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private GameObject _projectile;
#if UNITY_PHYSICS_3D
        [SerializeField] private Rigidbody _controller;
#endif
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

#if UNITY_PHYSICS_3D
            _controller.rotation = Quaternion.Euler(0, state.rotation, 0);
#if UNITY_6000
            var vel = _controller.linearVelocity;
            vel.x = moveVector.x;
            vel.z = moveVector.z;
            _controller.linearVelocity = vel;
#else
            var vel = _controller.velocity;
            vel.x = moveVector.x;
            vel.z = moveVector.z;
            _controller.velocity = vel;
#endif
#endif
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
