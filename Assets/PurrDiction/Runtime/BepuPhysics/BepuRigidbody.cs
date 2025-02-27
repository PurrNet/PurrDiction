using System;
using BEPUphysics.Entities;
using BEPUutilities;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;
using Space = BEPUphysics.Space;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/BEPU/Bepu Rigidbody")]
    public partial class BepuRigidbody : PredictedIdentity<BepuRigidbody.BepuRigidbodyState>
    {
        [SerializeField, HideInInspector] private BepuColliderDefinition[] _colliders;
        [SerializeField, HideInInspector] private bool _isTrigger;
        [SerializeField, HideInInspector] private bool _isKinematic;
        [SerializeField, HideInInspector] private FP _mass = FP.C1;

        [SerializeField, HideInInspector] private FP _linearDrag = FP.C0;
        [SerializeField, HideInInspector] private FP _angularDrag = FP.C0p15;

        private Entity _entity;
        private Space _space;
        public Action onEntityCreated;

        public Entity entity => _entity;

        internal override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn)
                return;

            _space = world.physics;

            if (_space == null)
            {
                PurrLogger.LogException($"To use BepuRigidbody you need to select <b>BEPUPhysics</b> as a provider in the PredictionManager.", this);
                base.Setup(manager, world, id);
                return;
            }

            CreateEntity(world);
            SetupConstraints();

            base.Setup(manager, world, id);
        }

        private void CreateEntity(PredictionManager world)
        {
            _entity = BepuColliderFactory.Create(transform, _colliders, mass);

            if (_isKinematic)
                _entity.BecomeKinematic();

            _entity.Tag = gameObject;

            _space.Add(_entity);
            InitializeCollisionHandler(world);
            UpdateTriggerState();
            onEntityCreated?.Invoke();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_colliders == null)
                return;

            BepuColliderFactory.DrawGizmos(transform, _colliders);
        }
#endif

        public struct BepuRigidbodyState : IPredictedData<BepuRigidbodyState>
        {
            public FPVector3 linearVelocity;
            public FPVector3 angularVelocity;
        }
    }
}
