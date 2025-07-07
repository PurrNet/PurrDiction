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

        protected override void GetFinalInput(ref TestNodeInput input)
        {
            input.isKeyPressed = Input.GetKey(KeyCode.X);
        }

        protected override void StateSimulate(in TestNodeInput input, ref TestNodeData testNodeData, float delta)
        {
            base.StateSimulate(delta);

            var state = currentState;
            currentState = state;
        }

        protected override void Simulate(TestNodeInput input, ref TestNodeData state, float delta)
        {
            if(state.wasKeyPressed != input.isKeyPressed)
            {
                state.wasKeyPressed = input.isKeyPressed;
                if(state.wasKeyPressed)
                    machine.Next();
            }
        }

        public struct TestNodeData : IPredictedData<TestNodeData>
        {
            public bool wasKeyPressed;

            public void Dispose() { }
        }

        public struct TestNodeInput : IPredictedData
        {
            public bool isKeyPressed;

            public void Dispose() { }
        }
    }
}
