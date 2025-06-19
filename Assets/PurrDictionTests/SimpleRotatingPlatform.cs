using PurrNet.Pooling;
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

        protected override void OnSpawned()
        {
            _predictedRigidbody.onCollisionEnter += OnUnityCollisionEnter;
        }

        protected override void OnDespawned()
        {
            _predictedRigidbody.onCollisionEnter -= OnUnityCollisionEnter;
        }

        protected override void Simulate(ref State data, float delta)
        {
            if (data.collisionCount < 5)
                return;

            predictionManager.hierarchy.Delete(this);
        }

        private void OnUnityCollisionEnter(PredictedRigidbody other, DisposableList<PhysicsContactPoint> evContacts)
        {
            var copy = currentState;
            copy.collisionCount += 1;
            currentState = copy;
        }
    }
}
