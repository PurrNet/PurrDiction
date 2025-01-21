using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.Input, SimpleRotatingPlatform.State>
    {
        [SerializeField] private Transform _visuals;
        [SerializeField] private float _rotationSpeed = 1;

        public struct Input : IPredictedData
        {
            public bool stopRotation;

            public override string ToString()
            {
                return $"stopRotation: {stopRotation}";
            }
        }
        
        public struct State : IPredictedData<State>
        {
            public Vector3 position;
            public float yRotation;
        }
        
        protected override State UpdateUnityState() => new()
        {
            position = transform.position,
            yRotation = transform.eulerAngles.y
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
        
        protected override void RollbackUnityState(State state)
        {
            transform.SetPositionAndRotation(state.position, Quaternion.Euler(0, state.yRotation, 0));
        }

        // optional step to update the view
        protected override void UpdateView(State predicted, State? verified)
        {
            _visuals.SetPositionAndRotation(predicted.position, Quaternion.Euler(0, predicted.yRotation, 0));
        }

        // optional step to interpolate between states
        protected override State Interpolate(State from, State to, float t)
        {
            var result = from;
            
            result.yRotation = Mathf.LerpAngle(from.yRotation, to.yRotation, t);
            result.position = Vector3.Lerp(from.position, to.position, t);
            
            return result;
        }
    }
}
