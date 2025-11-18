using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct FallingSandInput : IPredictedData
    {
        public int? cellToActivate;
        public bool useBrush;
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

        protected override void UpdateInput(ref FallingSandInput input)
        {
            input.useBrush |= Input.GetMouseButtonDown(1);
        }

        protected override void GetFinalInput(ref FallingSandInput input)
        {
            bool leftClick = Input.GetMouseButton(0);

            if (leftClick)
            {
                input.cellToActivate = _events.isClicking ? _events.clickingIndex : null;
                input.useBrush = false;
            }
            else if (input.useBrush)
            {
                input.cellToActivate = _events.clickingIndex;
            }
            else input.cellToActivate = null;
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
            {
                if (input.useBrush)
                    _fallingSand.PlaceBrush(input.cellToActivate.Value);
                else _fallingSand.SetGridValue(input.cellToActivate.Value, new SandTile
                {
                    color = new ByteColor()
                });
            }
        }
    }
}
