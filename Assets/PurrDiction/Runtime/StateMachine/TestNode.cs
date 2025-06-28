using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class TestNode : PredictedStateNode<TestNode.TestNodeData>
    {
        public override void Enter()
        {
            //Happens within simulation
            Debug.Log($"Entered state {gameObject.name}", machine);
        }

        protected override void StateSimulate(ref TestNodeData state, float delta)
        {
            //Happens within simulation
        }

        public override void Exit()
        {
            //Happens within simulation
            Debug.Log($"Exit state {gameObject.name}");
        }

        public struct TestNodeData : IPredictedData<TestNodeData>
        {
            public void Dispose() { }
        }
    }
}
