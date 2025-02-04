using BEPUphysics.Entities;
using BEPUutilities;
using ConversionHelper;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct BepuRigidbodyState : IPredictedData<BepuRigidbodyState>
    {
        public FPVector3 position;
        public FPQuaternion orientation;
        public FPVector3 linearVelocity;
        public FPVector3 angularVelocity;
    }
    
    public class BepuRigidbody : PredictedIdentity<BepuRigidbodyState>
    {
        [Header("Bepu Rigidbody")]
        [SerializeField] private BepuColliderDefinition[] _colliders;
        
        private Entity _entity;

        protected override BepuRigidbodyState GetInitialState()
        {
            return new BepuRigidbodyState
            {
                position = MathConverter.Convert(transform.position),
                orientation = MathConverter.Convert(transform.rotation),
                linearVelocity = default,
                angularVelocity = default
            };
        }
    }
}
