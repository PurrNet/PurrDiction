namespace PurrNet.Prediction
{
    public struct PredictedGameObjectState : IPredictedData<PredictedGameObjectState>
    {
        public bool isActive;

        public void Dispose() { }

        public override string ToString()
        {
            return $"isActive={isActive}";
        }
    }

    public class PredictedGameObject : PredictedIdentity<PredictedGameObjectState>
    {
        protected override void GetUnityState(ref PredictedGameObjectState state)
        {
            state.isActive = gameObject.activeSelf;
        }

        protected override void SetUnityState(PredictedGameObjectState state)
        {
            gameObject.SetActive(state.isActive);
        }

        public void SetActive(bool active)
        {
            ref var state = ref currentState;
            if (active == state.isActive) return;
            state.isActive = active;
            SetUnityState(state);
        }
    }
}
