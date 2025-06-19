namespace PurrNet.Prediction
{
    public class PredictedTime : PredictedIdentity<PredictedTimeState>
    {
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

        protected override void Simulate(ref PredictedTimeState state, float delta)
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
