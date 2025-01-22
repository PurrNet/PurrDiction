using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictionState : IPredictedData<PredictionState>
    {
        public PlayerID? owner;
        public PredictionTransformState? transform;
        
        public static PredictionState Interpolate(PredictionState from, PredictionState to, float t)
        {
            var result = from;
            
            if (result.transform == null)
                return result;
            
            if (to.transform == null)
                return result;

            var state = result.transform.Value;
            var targetState = to.transform.Value;
            
            state.position = Vector3.Lerp(state.position, targetState.position, t);
            state.rotation = Quaternion.Slerp(state.rotation, targetState.rotation, t);
            result.transform = state;
            
            return result;
        }
        public override string ToString()
        {
            return $"(owner: {owner}, transform: {transform?.rotation})";
        }
    }
}