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
        TestFallingSandRenderer _renderer;
        TestFallingSand _fallingSand;

        private void OnEnable()
        {
            _renderer = InstanceHandler.GetInstance<TestFallingSandRenderer>();
            _fallingSand = _renderer.sand;
            _renderer.onClicked += ClickedCell;
        }

        private void OnDisable()
        {
            _renderer.onClicked -= ClickedCell;
        }

        private void ClickedCell(int index)
        {
            _cell = index;
        }

        private int? _cell;

        protected override void GetFinalInput(ref FallingSandInput input)
        {
            input.cellToActivate = _cell;
            _cell = null;
        }

        protected override void Simulate(FallingSandInput input, ref FallingSandPlayerState state, float delta)
        {
            if (input.cellToActivate.HasValue)
                _fallingSand.SetGridValue(input.cellToActivate.Value);
        }
    }
}
