using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedTime : PredictedIdentity<PredictedTimeState>
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

        protected override void Simulate(ref PredictedTimeState state, float delta)
        {
            state.tick += 1;
        }

        protected override bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            return deltaModule.WriteReliableWithModifier(packer, target, stateKey, fullPredictedState.state, ModifyOldValue);
        }

        protected override void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, ref PredictedTimeState state)
        {
            deltaModule.ReadReliableWithModifier(packer, stateKey, ref state, ModifyOldValue);
        }

        static void ModifyOldValue(ref PredictedTimeState state)
        {
            // we can accurately predict the new value and decrease bandwidth here so why not
            state.tick += 1;
        }

        protected override PredictedTimeState Interpolate(PredictedTimeState from, PredictedTimeState to, float t)
        {
            return to;
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
