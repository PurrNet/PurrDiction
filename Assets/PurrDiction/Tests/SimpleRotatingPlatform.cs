using FixMath.NET;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.Input, SimpleRotatingPlatform.State>
    {
        [SerializeField] private Transform _visuals;
        [SerializeField] private float _rotationSpeed = 1;

        public struct Input : IPackedAuto, IOptionalDispose
        {
            public bool stopRotation;
        }
        
        public struct State : IPackedAuto, IOptionalDispose
        {
            public Vector3 position;
            public Quaternion rotation;
        }
        
        protected override State GetCurrentState() => new()
        {
            position = transform.position,
            rotation = transform.rotation
        };

        protected override Input GetInput() => new()
        {
            stopRotation = UnityEngine.Input.GetKey(KeyCode.Space)
        };

        protected override void Simulate(Input? input, Fix64 delta)
        {
            if (input?.stopRotation == true)
                return;
            
            transform.rotation *= Quaternion.Euler(0, _rotationSpeed * (float)delta, 0);
        }
        
        protected override void Rollback(State state)
        {
            transform.SetPositionAndRotation(state.position, state.rotation);
        }

        // optional step to update the view
        protected override void UpdateView(State predicted, State? verified)
        {
            _visuals.SetPositionAndRotation(predicted.position, predicted.rotation);
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
