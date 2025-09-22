using UnityEngine;

namespace PurrNet.Prediction.Prebuilt
{
    [AddComponentMenu("PurrDiction/Prebuilt/Rigidbody/Projectile shooter")]
    public class RigidbodyShooter : PredictedIdentity<RigidbodyShooter.ShootInput, RigidbodyShooter.ShootData>
    {
        [SerializeField] private GameObject projectile;
        [SerializeField] private KeyCode shootKey = KeyCode.Mouse0;
        [SerializeField] private float shootCooldown = 0.5f;
        [SerializeField] private Vector3 spawnOffset = Vector3.forward;
        [SerializeField] private float projectileInitialVelocity = 10;

#if UNITY_EDITOR
        [Header("Debug")]
        [SerializeField] private bool drawGizmos = true;
#endif

        protected override ShootData GetInitialState()
        {
            var state = new ShootData()
            {
                timeSinceShot = shootCooldown
            };
            return state;
        }

        protected override void Simulate(ShootInput input, ref ShootData state, float delta)
        {
            state.timeSinceShot += delta;
            if (input.shoot && state.timeSinceShot >= shootCooldown)
            {
                Shoot();
                state.timeSinceShot = 0;
            }
        }

        private void Shoot()
        {
#if UNITY_PHYSICS_3D
            if (!projectile)
                return;

            var pos = transform.TransformPoint(spawnOffset);

            var projectileId = hierarchy.Create(projectile.gameObject, pos, transform.rotation);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
            if(projectileRb)
                projectileRb.linearVelocity = transform.forward * projectileInitialVelocity;
            else
                PurrNet.Logging.PurrLogger.LogError($"Failed to get Rigidbody component from projectile ({projectile.gameObject.name})", projectile);
#endif
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.TransformPoint(spawnOffset), 0.2f);
        }
#endif

        protected override void GetFinalInput(ref ShootInput input)
        {
            input = new ShootInput()
            {
                shoot = Input.GetKey(shootKey)
            };
        }

        public struct ShootData : IPredictedData<ShootData>
        {
            public float timeSinceShot;

            public void Dispose() { }
        }

        public struct ShootInput : IPredictedData
        {
            public bool shoot;

            public void Dispose() { }
        }
    }
}
