using System;
using FixMath.NET;
using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class TestNode : PredictedStateNode<TestNodeInput, TestNodeData>
    {
        public override void Enter()
        {
            Debug.Log($"Entered state {gameObject.name}");
        }

        public override void Exit()
        {
            Debug.Log($"Exit state {gameObject.name}");
        }

        protected override TestNodeInput GetInput()
        {
            var input = new TestNodeInput()
            {
                isKeyPressed = Input.GetKey(KeyCode.X)
            };
            
            return input;
        }

        public override void StateSimulate(FP delta)
        {
            base.StateSimulate(delta);

            var state = currentState;
            state.testValue += (float)delta;
            currentState = state;
        }

        protected override void Simulate(TestNodeInput? input, ref TestNodeData state, FP delta)
        {
            if (!input.HasValue)
                return;
            
            if(state.wasKeyPressed != input.Value.isKeyPressed)
            {
                state.wasKeyPressed = input.Value.isKeyPressed;
                if(state.wasKeyPressed)
                    machine.Next();
            }
        }
    }

    public struct TestData
    {
        public float testValue;

        public override string ToString()
        {
            return testValue.ToString();
        }
    }
    
    public struct TestNodeData : IPredictedData<TestNodeData>
    {
        public bool wasKeyPressed;
        public float testValue;
    }

    public struct TestNodeInput : IPredictedData
    {
        public bool isKeyPressed;
    }
}
