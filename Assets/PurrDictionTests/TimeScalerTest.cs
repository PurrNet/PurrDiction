using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TimeScalerTest : StatelessPredictedIdentity
    {
        [SerializeField] private float _targetTimeScale = 1f;

        protected override void Simulate(float delta)
        {
            Time.timeScale = _targetTimeScale;
        }
    }
}
