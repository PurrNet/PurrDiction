using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct FallingSandInput : IPredictedData
    {
        public int? cellToActivate;
        public void Dispose() { }
    }

    public struct FallingSandPlayerState : IPredictedData<FallingSandPlayerState>
    {
        public void Dispose() { }
    }

    public class TestFallingSandPlayer : PredictedIdentity<FallingSandInput, FallingSandPlayerState>
    {
        private TestFallingSand _fallingSand;
        private FallingSandEvents _events;

        private void OnEnable()
        {
            _events = InstanceHandler.GetInstance<FallingSandEvents>();
            _fallingSand = _events.sand;
        }

        protected override void GetFinalInput(ref FallingSandInput input)
        {
            input.cellToActivate = _events.isClicking ? _events.clickingIndex : null;
        }

        [SerializeField] private bool _interceptInput;

        protected override void SanitizeInput(ref FallingSandInput input)
        {
            if (_interceptInput)
                input.cellToActivate = null;
        }

        protected override void Simulate(FallingSandInput input, ref FallingSandPlayerState state, float delta)
        {
            if (input.cellToActivate.HasValue)
                _fallingSand.SetGridValue(input.cellToActivate.Value);
        }
    }
}
