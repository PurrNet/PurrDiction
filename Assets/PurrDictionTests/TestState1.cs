using System;
using System.Collections.Generic;
using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TestState1 : PredictedStateNode<TestState1.StateData>
    {
        public static List<TestState1> Instances = new();
        
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
            
            Debug.Log($"View entered state: {gameObject.name} | {isVerified}");
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

        public override void Enter()
        {
            base.Enter();
            Debug.Log($"Entered state: {gameObject.name}");
        }

        public struct StateData : IPredictedData<StateData>
        {
            
        }
    }
}
