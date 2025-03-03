using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleRotatingPlatform : PredictedIdentity<SimpleRotatingPlatform.State>
    {
        [SerializeField] private PredictedRigidbody _predictedRigidbody;

        public struct State : IPredictedData<State>
        {
            public int collisionCount;
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
            if (data.collisionCount < 3)
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
