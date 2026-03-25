using System;
using System.Collections.Generic;
using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TestState : PredictedStateNode<TestState.StateData>
    {
        public static List<TestState> Instances = new();

        private void Awake()
        {
            Instances.Add(this);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Instances.Remove(this);
        }

        public override void ViewEnter(bool isVerified)
        {
            base.ViewEnter(isVerified);

            //Debug.Log($"View entered state: {gameObject.name} | {isVerified}");
        }

        protected override void StateUpdateView(StateData predictedState, StateData? validatedState)
        {
            base.StateUpdateView(predictedState, validatedState);
            //Debug.Log($"Updating view for {gameObject.name}");
        }

        public static void NextState()
        {
            Instances[0].machine.Next();
        }

        [ContextMenu("Force next state")]
        private void ForceNextState()
        {
            NextState();
        }

        [ContextMenu("Force this state")]
        private void ForceThisState()
        {
            machine.SetState(this);
        }

        public struct StateData : IPredictedData<StateData>
        {
            public void Dispose() { }
        }
    }
}
