using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.State>
    {
        [SerializeField] private float _rotationSpeed = 1;
        
        public struct State
        {
            public Vector3 position;
            public Quaternion rotation;
        }
        
        protected override State InitializeState()
        {
            return new State
            {
                position = transform.position,
                rotation = transform.rotation
            };
        }
        
        protected override void Simulate(ref State state)
        {
            state.rotation *= Quaternion.Euler(0, _rotationSpeed, 0);
        }

        // optional step to update the view
        public override void UpdateView(State predicted, State? verified)
        {
            transform.SetPositionAndRotation(predicted.position, predicted.rotation);
        }

        // optional step to interpolate between states
        protected override State Interpolate(State from, State to, float t)
        {
            var result = from;
            
            result.rotation = Quaternion.Slerp(from.rotation, to.rotation, t);
            result.position = Vector3.Lerp(from.position, to.position, t);
            
            return result;
        }
    }
}
