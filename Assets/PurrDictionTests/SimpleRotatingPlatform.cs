using System;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.State>
    {
        public struct State : IPredictedData<State>
        {
            public bool markedForDeletion;
        }

        protected override void Simulate(Fix64 delta, ref State data)
        {
            if (!data.markedForDeletion)
                return;
            
            predictionManager.hierarchy.Delete(this);
        }


        private void OnCollisionEnter(Collision other)
        {
            var copy = currentState;
            copy.markedForDeletion = true;
            currentState = copy;
        }
    }
}
