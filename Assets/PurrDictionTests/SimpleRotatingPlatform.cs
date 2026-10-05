using System;
using PurrNet.Logging;
using UnityEngine;
using Random = UnityEngine.Random;

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

#if UNITY_PHYSICS_3D
        protected override void LateAwake()
        {
            _predictedRigidbody.onPredictedCollisionEnter += OnUnityCollisionEnter;
            _predictedRigidbody.onPredictedTriggerEnter += OnUnityTriggerEnter;
        }

        protected override void Destroyed()
        {
            _predictedRigidbody.onPredictedCollisionEnter -= OnUnityCollisionEnter;
            _predictedRigidbody.onPredictedTriggerEnter -= OnUnityTriggerEnter;
        }
#endif

        protected override void Simulate(ref State data, float delta)
        {
            /*if (data.collisionCount < 5)
                return;

            predictionManager.hierarchy.Delete(gameObject);*/
        }

        private void OnUnityTriggerEnter(PredictedTrigger trigger)
        {
            PurrLogger.Log($"Triggered with {trigger.other} on {gameObject.name}");
        }

        private void OnUnityCollisionEnter(PredictedCollision collision)
        {
            PurrLogger.Log($"Collided with {collision.other} on {gameObject.name}");
            currentState.collisionCount += 1;
        }
    }
}
