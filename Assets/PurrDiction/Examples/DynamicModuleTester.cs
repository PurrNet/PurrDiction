using System;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct DynamicTesterInput : IPredictedData, IEquatable<DynamicTesterInput>
    {
        public bool spawnDynamicTimer;
        public bool startStaticTimer;

        public void Dispose() { }

        public bool Equals(DynamicTesterInput other) => spawnDynamicTimer == other.spawnDynamicTimer && startStaticTimer == other.startStaticTimer;
        public override bool Equals(object obj) => obj is DynamicTesterInput o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(spawnDynamicTimer, startStaticTimer);
        public override string ToString() => $"spawnDynamicTimer: {spawnDynamicTimer}, startStaticTimer: {startStaticTimer}";
    }

    public struct DynamicTesterState : IPredictedData<DynamicTesterState>
    {
        public void Dispose() { }
    }

    public class DynamicModuleTester : PredictedIdentity<DynamicTesterInput, DynamicTesterState>
    {
        [SerializeField] private KeyCode _spawnDynamicKey = KeyCode.T;
        [SerializeField] private KeyCode _startStaticKey = KeyCode.S;

        private TimerModule _staticTimer;
        private TimerModule _dynamicTimer;

        protected override void LateAwake()
        {
            base.LateAwake();
            _staticTimer = new TimerModule(this);
            _staticTimer.onPredictedTimerUpdated_View += t => PurrLogger.Log($"[DynamicModuleTester/static] predicted timer: {t:F2}s");
            _staticTimer.onTimerEnded += () => PurrLogger.Log("[DynamicModuleTester/static] Static timer ended.");
        }

        protected override void GetFinalInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer = Input.GetKey(_spawnDynamicKey);
            input.startStaticTimer = Input.GetKey(_startStaticKey);
        }

        protected override void UpdateInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer |= Input.GetKeyDown(_spawnDynamicKey);
            input.startStaticTimer |= Input.GetKeyDown(_startStaticKey);
        }

        protected override void ModifyExtrapolatedInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer = false;
            input.startStaticTimer = false;
        }

        protected override void Simulate(DynamicTesterInput input, ref DynamicTesterState state, float delta)
        {
            if (input.startStaticTimer && !_staticTimer.isTimerRunning)
            {
                _staticTimer.StartTimer(2f);
                PurrLogger.Log("[DynamicModuleTester/static] Started static timer at 2s.");
            }

            if (input.spawnDynamicTimer && _dynamicTimer == null)
            {
                _dynamicTimer = new TimerModule(this);
                var timer = _dynamicTimer;
                timer.onTimerEnded += HandleDynamicTimerEnded;
                timer.onPredictedTimerUpdated_View += t => PurrLogger.Log($"[DynamicModuleTester/dynamic] predicted timer: {t:F2}s");
                timer.onDisposed += () => { if (_dynamicTimer == timer) _dynamicTimer = null; };
                timer.StartTimer(1f);
                PurrLogger.Log("[DynamicModuleTester/dynamic] Spawned TimerModule mid-simulation.");
            }
        }

        private void HandleDynamicTimerEnded()
        {
            PurrLogger.Log("[DynamicModuleTester/dynamic] Timer ended. Removing module.");
            var timer = _dynamicTimer;
            _dynamicTimer = null;
            timer?.Dispose();
        }
    }
}
