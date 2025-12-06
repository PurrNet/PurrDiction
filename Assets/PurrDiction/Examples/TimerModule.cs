using System;

namespace PurrNet.Prediction.Tests
{
    public class TimerModule : PredictedModule<TimerState>
    {
        public event Action onTimerEnded;
        public bool isTimerRunning => currentState.timer.HasValue;
        
        public TimerModule(PredictedIdentity identity) : base(identity) { }

        public void StartTimer(float timer)
        {
            currentState.timer = timer;
        }

        protected override void Simulate(ref TimerState state, float delta)
        {
            if (!state.timer.HasValue)
                return;

            state.timer -= delta;
            if (state.timer <= 0)
            {
                onTimerEnded?.Invoke();
                state.timer = null;
            }
        }
    }

    public struct TimerState : IPredictedData<TimerState>
    {
        public float? timer;

        public void Dispose()
        {
            timer = null;
        }
    }
}
