using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private CharacterController _controller;
        [SerializeField] private float _speed = 5;

        protected override void Simulate(SimpleWASDInput? input, ref SimpleCCState state, Fix64 delta)
        {
            var move = new Vector3(input?.horizontal ?? 0, 0, input?.vertical ?? 0);
            
            if (move.magnitude > 0)
                move.Normalize();
            
            var moveVector = move * _speed * (float)delta;
            
            _controller.Move(moveVector);
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
