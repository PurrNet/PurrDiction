using System;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private CharacterController _controller;
        [SerializeField] private Transform _visuals;
        [SerializeField] private float _speed = 5;
        
        private SimpleCCState _state;

        protected override SimpleCCState GetCurrentState()
        {
            _state.position = transform.position;
            return _state;
        }

        protected override void Rollback(SimpleCCState state)
        {
            _state = state;
            
            _controller.enabled = false;
            transform.position = state.position;
            _controller.enabled = true;
        }

        protected override void Simulate(SimpleWASDInput? input, Fix64 delta)
        {
            var move = new Vector3(input?.horizontal ?? 0, 0, input?.vertical ?? 0);
            
            if (move.magnitude > 0)
                move.Normalize();
            
            var moveVector = move * _speed * (float)delta;
            
            _controller.Move(moveVector);
        }
        
        protected override void UpdateView(SimpleCCState predicted, SimpleCCState? verified)
        {
            _visuals.position = predicted.position;
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
