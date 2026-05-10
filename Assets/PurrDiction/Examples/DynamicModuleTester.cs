using System;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct DynamicTesterInput : IPredictedData, IEquatable<DynamicTesterInput>
    {
        public bool spawnDynamicTimer;

        public void Dispose() { }

        public bool Equals(DynamicTesterInput other) => spawnDynamicTimer == other.spawnDynamicTimer;
        public override bool Equals(object obj) => obj is DynamicTesterInput o && Equals(o);
        public override int GetHashCode() => spawnDynamicTimer.GetHashCode();
        public override string ToString() => $"spawnDynamicTimer: {spawnDynamicTimer}";
    }

    public struct DynamicTesterState : IPredictedData<DynamicTesterState>
    {
        public void Dispose() { }
    }

    public class DynamicModuleTester : PredictedIdentity<DynamicTesterInput, DynamicTesterState>
    {
        [SerializeField] private KeyCode _spawnDynamicKey = KeyCode.T;

        protected override void GetFinalInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer = Input.GetKey(_spawnDynamicKey);
        }

        protected override void UpdateInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer |= Input.GetKeyDown(_spawnDynamicKey);
        }

        protected override void ModifyExtrapolatedInput(ref DynamicTesterInput input)
        {
            input.spawnDynamicTimer = false;
        }

        protected override void Simulate(DynamicTesterInput input, ref DynamicTesterState state, float delta)
        {
            if (input.spawnDynamicTimer && !TryGetModule<TimerModule>(out _))
            {
                var timer = new TimerModule(this);
                timer.onTimerEnded += HandleDynamicTimerEnded;
                timer.onPredictedTimerUpdated_View += t => PurrLogger.Log($"[DynamicModuleTester] predicted timer: {t:F2}s");
                timer.StartTimer(1f);
                PurrLogger.Log("[DynamicModuleTester] Spawned TimerModule mid-simulation.");
            }
        }

        private void HandleDynamicTimerEnded()
        {
            if (TryGetModule<TimerModule>(out var timer))
            {
                PurrLogger.Log("[DynamicModuleTester] Timer ended. Disposing module.");
                timer.Dispose();
            }
        }
    }
}
