using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class TimerModule : PredictedModule<TimerState>
    {
        /// <summary>
        /// Returns the predicted time to use for view
        /// </summary>
        public float predictedViewTimer { get; private set; }
        
        /// <summary>
        /// Returns the verified time to use for view
        /// </summary>
        public float verifiedViewTimer { get; private set; }

        /// <summary>
        /// Invoked when the view updates, this returns predicted-view. Good for visual use.
        /// </summary>
        public event Action<float> onPredictedTimerUpdated_View;
        
        /// <summary>
        /// Invoked when the view updated comes in with verified data. Should be once per frame
        /// </summary>
        public event Action<float> onVerifiedTimerUpdated_View;
        
        /// <summary>
        /// Invoked when the timer is stopped
        /// </summary>
        public event Action onTimerEnded;
        
        /// <summary>
        /// Whether the timer is currently running or not
        /// </summary>
        public bool isTimerRunning => currentState.timer.HasValue;

        /// <summary>
        /// Returns the current timer value
        /// </summary>
        public float remaining => currentState.timer.HasValue ? currentState.timer.Value : 0;

        private float _lastPredictedViewTime, _lastVerifiedViewTime;
        private bool _manualTick;

        /// <summary>
        /// Constructs the timer module with the given settings.
        /// </summary>
        /// <param name="identity">The predicted identity which this module is linked to</param>
        /// <param name="manualTick">Whether it should automatically count down, or you want to handle the ticking of the timer</param>
        public TimerModule(PredictedIdentity identity, bool manualTick = false) : base(identity)
        {
            _manualTick = manualTick;
        }

        /// <summary>
        /// Starts the timer
        /// </summary>
        /// <param name="timer">Initial value for the timer</param>
        public void StartTimer(float timer)
        {
            currentState.timer = timer;
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

            TickTimer(-delta);
        }

        /// <summary>
        /// Ticking the timer manually, all you need is the TimerState for reference and the amount to tick. Typically just -delta would do the trick
        /// </summary>
        /// <param name="state">The currentState of the module</param>
        /// <param name="tick">The amount to move the timer by</param>
        /// <param name="autoStopOnDownTick">Whether it should end the timer when hitting 0</param>
        public void TickTimer(float tick, bool autoStopOnDownTick = true)
        {
            if (!currentState.timer.HasValue)
                return;

            currentState.timer += tick;
            if (currentState.timer <= 0)
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
