using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.State>
    {
        [SerializeField] private MeshRenderer _renderer;
        [SerializeField] private PredictedRigidbody _predictedRigidbody;

        private void Reset()
        {
            _predictedRigidbody = GetComponentInChildren<PredictedRigidbody>();
            _renderer = GetComponentInChildren<MeshRenderer>();
        }

        public struct State : IPredictedData<State>
        {
            public int collisionCount;

            public override string ToString()
            {
                return $"Collision count: {collisionCount}";
            }

            public void Dispose() { }
        }

        private void Awake()
        {
            _renderer.material.color = Random.ColorHSV();
        }

        protected override void LateAwake()
        {
            _predictedRigidbody.onCollisionEnter += OnUnityCollisionEnter;
            _predictedRigidbody.onTriggerEnter += OnUnityTriggerEnter;
        }

        protected override void Destroyed()
        {
            _predictedRigidbody.onCollisionEnter -= OnUnityCollisionEnter;
            _predictedRigidbody.onTriggerEnter -= OnUnityTriggerEnter;
        }

        protected override void Simulate(ref State data, float delta)
        {
            if (data.collisionCount < 5)
                return;

            predictionManager.hierarchy.Delete(gameObject);
        }

        private void OnUnityTriggerEnter(GameObject other)
        {
            PurrLogger.Log($"Triggered with {other} on {gameObject.name}");
        }

        private void OnUnityCollisionEnter(GameObject other, PhysicsCollision collision)
        {
            var copy = currentState;
            copy.collisionCount += 1;
            currentState = copy;
        }
    }
}
