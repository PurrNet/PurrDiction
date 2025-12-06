using System;
using UnityEngine;

namespace PurrNet.Prediction
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

        /// <summary>
        /// Starts the timer
        /// </summary>
        /// <param name="timer">Initial value for the timer</param>
        /// <param name="manualTick">Whether it should automatically count down, or you want to handle the ticking of the timer</param>
        public void StartTimer(float timer, bool manualTick = false)
        {
            currentState.timer = timer;
            _manualTick = manualTick;
        }

        /// <summary>
        /// Stops the timer immediately
        /// </summary>
        /// <param name="silent">Whether the onTimerEnded action should not call</param>
        public void StopTimer(bool silent = false)
        {
            currentState.timer = null;
            if(!silent)
                onTimerEnded?.Invoke();
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
            if (_manualTick)
                return;

            TickTimer(ref state, -delta);
        }

        /// <summary>
        /// Ticking the timer manually, all you need is the TimerState for reference and the amount to tick. Typically just -delta would do the trick
        /// </summary>
        /// <param name="state">The currentState of the module</param>
        /// <param name="tick">The amount to move the timer by</param>
        /// <param name="autoStopOnDownTick">Whether it should end the timer when hitting 0</param>
        public void TickTimer(ref TimerState state, float tick, bool autoStopOnDownTick = true)
        {
            if (!state.timer.HasValue)
                return;

            state.timer = tick;
            if (state.timer <= 0)
            {
                StopTimer();
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
