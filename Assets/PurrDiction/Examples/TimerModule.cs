using System;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TimerModule : PredictedModule<TimerState>
    {
        public float predictedViewTimer { get; private set; }
        public float verifiedViewTimer { get; private set; }

        public event Action<float> onPredictedTimerUpdated_View;
        public event Action<float> onVerifiedTimerUpdated_View;
        public event Action onTimerEnded;
        public bool isTimerRunning => currentState.timer.HasValue;

        private float _lastPredictedViewTime, _lastVerifiedViewTime;
        private bool _manualTick;
        
        public TimerModule(PredictedIdentity identity) : base(identity) { }

        public void StartTimer(float timer, bool manualTick = false)
        {
            currentState.timer = timer;
            _manualTick = manualTick;
        }

        protected override void UpdateView(TimerState viewState, TimerState? verifiedState)
        {
            base.UpdateView(viewState, verifiedState);
            if (!viewState.timer.HasValue)
                predictedViewTimer = 0;
            else
                predictedViewTimer = viewState.timer.Value;
            
            if (verifiedState.HasValue)
            {
                if (!verifiedState.Value.timer.HasValue)
                    verifiedViewTimer = 0;
                else
                    verifiedViewTimer = verifiedState.Value.timer.Value;
            }
            
            if(!Mathf.Approximately(_lastPredictedViewTime, predictedViewTimer))
                onPredictedTimerUpdated_View?.Invoke(predictedViewTimer);

            if(!Mathf.Approximately(_lastVerifiedViewTime, verifiedViewTimer))
                onVerifiedTimerUpdated_View?.Invoke(verifiedViewTimer);
            
            _lastPredictedViewTime = predictedViewTimer;
            _lastVerifiedViewTime = verifiedViewTimer;
        }

        protected override void Simulate(ref TimerState state, float delta)
        {
            if (!_manualTick)
                return;

            TickTimer(ref state, delta);
        }

        public void TickTimer(ref TimerState state, float delta)
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
