using PurrNet.Prediction;
using PurrNet.Prediction.StateMachine;
using PurrNet.Prediction.Tests;
using PurrNet.Utils;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class TestStateNode : PredictedStateNode<SimpleWASDInput, TestStateNode.State>
    {
        [SerializeField] private GameObject _projectile;

        private TimerModule _timer;

        [PurrReadOnly, SerializeField] private float _predictedTimer, _verifiedTimer;

        protected override void LateAwake()
        {
            base.LateAwake();
            _timer = new TimerModule(this);
            _timer.onPredictedTimerUpdated_View += time => _predictedTimer = time;
            _timer.onVerifiedTimerUpdated_View += time => _verifiedTimer = time;
        }

        protected override void Simulate(SimpleWASDInput input, ref State state, float delta)
        {
            if (input.jump && !_timer.isTimerRunning)
                Shoot();
        }

        private void Shoot()
        {
            _timer.StartTimer(0.5f);
#if UNITY_PHYSICS_3D
            var pos = transform.position + transform.forward;
            var projectileId = hierarchy.Create(_projectile, pos, transform.rotation);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
#if UNITY_6000
            projectileRb.linearVelocity = transform.forward * 10;
#else
            projectileRb.velocity = transform.forward * 10;
#endif
#endif
        }

        protected override void ModifyExtrapolatedInput(ref SimpleWASDInput input)
        {
            input.jump = false;
            input.dash = false;
        }

        protected override void GetFinalInput(ref SimpleWASDInput input)
        {
            input.horizontal = Input.GetAxisRaw("Horizontal");
            input.vertical = Input.GetAxisRaw("Vertical");
            input.dash = Input.GetKey(KeyCode.LeftShift);
            input.jump = Input.GetKey(KeyCode.Space);
        }

        public struct State : IPredictedData<State>
        {
            public void Dispose() { }
        }
    }
}
