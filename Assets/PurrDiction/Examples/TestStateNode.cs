using PurrNet.Prediction;
using PurrNet.Prediction.StateMachine;
using PurrNet.Prediction.Tests;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class TestStateNode : PredictedStateNode<SimpleWASDInput, TestStateNode.State>
    {
        [SerializeField] private GameObject _projectile;

        public struct State : IPredictedData<State>
        {
            public float time;

            public void Dispose() { }
        }

        protected override void Simulate(SimpleWASDInput input, ref State state, float delta)
        {
            if (input.jump)
            {
                Shoot();
                Shoot();
            }
        }

        private void Shoot()
        {
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
        }

        protected override void UpdateInput(ref SimpleWASDInput input)
        {
            input.jump |= Input.GetKeyDown(KeyCode.Space);
        }
    }
}
