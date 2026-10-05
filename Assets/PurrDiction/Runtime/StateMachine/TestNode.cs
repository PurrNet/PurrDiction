using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class TestNode : PredictedStateNode<TestNode.TestNodeData>
    {
        public override void Enter()
        {
            Debug.Log($"Entered state {gameObject.name}", machine);
        }

        protected override void StateSimulate(ref TestNodeData state, float delta)
        {
        }

        public override void Exit()
        {
            Debug.Log($"Exit state {gameObject.name}");
        }

        public struct TestNodeData : IPredictedData<TestNodeData>
        {
            public void Dispose() { }
        }
    }
}
