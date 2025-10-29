using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedTime : DeterministicIdentity<PredictedTimeState>
    {
        public float timeScale
        {
            get => currentState.timeScale;
            set
            {
                ref var state = ref currentState;
                state.timeScale = value;
                Time.timeScale = value;
            }
        }

        public ulong tick => currentState.tick;

        public float time => tick * predictionManager.tickDelta;

        public float deltaTime => predictionManager.tickDelta;

        public float TicksToTime(ulong ticks)
        {
            return ticks * predictionManager.tickDelta;
        }

        public ulong TimeToTicks(float time)
        {
            return (ulong)(time / predictionManager.tickDelta);
        }

        protected override void GetUnityState(ref PredictedTimeState state)
        {
            state.timeScale = Time.timeScale;
        }

        protected override void SetUnityState(PredictedTimeState state)
        {
            Time.timeScale = state.timeScale;
        }

        protected override void Simulate(ref PredictedTimeState state, sfloat delta)
        {
            state.tick += 1;
        }

        protected override PredictedTimeState Interpolate(PredictedTimeState from, PredictedTimeState to, float t)
        {
            return to;
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
