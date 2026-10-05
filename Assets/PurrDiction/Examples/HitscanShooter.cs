#if UNITY_PHYSICS_3D
using PurrNet.Prediction;
using UnityEngine;

namespace PurrDiction.Examples
{
    /// <summary>
    /// Lag-compensated hitscan. The ray is tested against collider rollback history at the tick the
    /// shooter was looking at, so targets are hit where the shooter saw them. Targets need a
    /// ColliderRollback component; nothing else is required on the shooter.
    /// </summary>
    public class HitscanShooter : PredictedIdentity<HitscanShooter.FireInput, HitscanShooter.HitscanState>
    {
        [SerializeField] private float _range = 100f;
        [SerializeField] private float _cooldown = 0.2f;
        [SerializeField] private LayerMask _hitMask = ~0;

        protected override void Simulate(FireInput input, ref HitscanState state, float delta)
        {
            if (state.cooldown > 0f)
            {
                state.cooldown -= delta;
                return;
            }

            if (!input.fire)
                return;

            state.cooldown = _cooldown;
            state.shots += 1;

            var ray = new Ray(transform.position, transform.forward);
            if (predictionManager.lagCompensation.Raycast(lagCompensationTick, ray, out var hit, _range, _hitMask))
            {
                state.hits += 1;
                state.lastHitPoint = hit.point;
            }
        }

        protected override void UpdateInput(ref FireInput input)
        {
            input.fire |= Input.GetMouseButtonDown(0);
        }

        public struct FireInput : IPredictedData
        {
            public bool fire;

            public void Dispose() { }
        }

        public struct HitscanState : IPredictedData<HitscanState>
        {
            public float cooldown;
            public int shots;
            public int hits;
            public Vector3 lastHitPoint;

            public void Dispose() { }
        }
    }
}
#endif
