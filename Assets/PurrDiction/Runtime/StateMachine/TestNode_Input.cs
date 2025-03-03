using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class TestNode_Input : PredictedStateNode<TestNode_Input.TestNodeInput, TestNode_Input.TestNodeData>
    {
        public override void Enter()
        {
            //Happens within simulation
            Debug.Log($"Entered state {gameObject.name}", machine);
        }

        public override void Exit()
        {
            //Happens within simulation
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

        public override void StateSimulate(float delta)
        {
            base.StateSimulate(delta);

            var state = currentState;
            currentState = state;
        }

        protected override void Simulate(TestNodeInput? input, ref TestNodeData state, float delta)
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

        public struct TestNodeData : IPredictedData<TestNodeData>
        {
            public bool wasKeyPressed;
        }

        public struct TestNodeInput : IPredictedData
        {
            public bool isKeyPressed;
        }
    }
}
