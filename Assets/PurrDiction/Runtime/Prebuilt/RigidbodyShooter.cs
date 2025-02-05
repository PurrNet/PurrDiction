using System;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.Prebuilt
{
    public class RigidbodyShooter : PredictedIdentity<RigidbodyShooter.ShootInput, RigidbodyShooter.ShootData>
    {
        [SerializeField] private GameObject projectile;
        [SerializeField] private KeyCode shootKey = KeyCode.Mouse0;
        [SerializeField] private float shootCooldown = 0.5f;
        [SerializeField] private Vector3 spawnOffset = Vector3.forward;
        [SerializeField] private float projectileInitialVelocity = 10;

        protected override ShootData GetInitialState()
        {
            var state = new ShootData()
            {
                timeSinceShot = (FP)shootCooldown
            };
            return state;
        }

        protected override void Simulate(ShootInput? input, ref ShootData state, FP delta)
        {
            if (!input.HasValue)
                return;
            
            state.timeSinceShot += delta;
            if (input.Value.shoot && state.timeSinceShot >= (FP)shootCooldown)
            {
                Shoot();
                state.timeSinceShot = 0;
            }
        }
        
        private void Shoot()
        {
            if (!projectile)
                return;
            
            var pos = transform.TransformPoint(spawnOffset);
            
            var projectileId = hierarchy.Create(projectile.gameObject, pos, transform.rotation);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
            if(projectileRb)
                projectileRb.linearVelocity = transform.forward * projectileInitialVelocity;
            else
                PurrLogger.LogError($"Failed to get Rigidbody component from projectile ({projectile.gameObject.name})", projectile);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.TransformPoint(spawnOffset), 0.2f);
        }

        protected override ShootInput GetInput()
        {
            var input = new ShootInput()
            {
                shoot = UnityEngine.Input.GetKey(shootKey)
            };
            return input;
        }

        public struct ShootData : IPredictedData<ShootData>
        {
            public FP timeSinceShot;
        }

        public struct ShootInput : IPredictedData
        {
            public bool shoot;
        }
    }
}
