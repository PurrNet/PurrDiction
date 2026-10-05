using PurrNet.Modules;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Physics queries against PurrNet's collider rollback history, addressed in prediction ticks.
    /// Pass <see cref="PredictedIdentity.lagCompensationTick"/> from inside Simulate to test the
    /// world exactly as the controlling player was seeing it when the input was produced.
    /// On the server the query is authoritative; on a client it runs against the client's own
    /// recorded history, which is what that client rendered, so hits can be predicted locally.
    /// Colliders take part when their object carries a <see cref="ColliderRollback"/> component.
    /// </summary>
    public sealed class PredictedLagCompensation
    {
        private readonly PredictionManager _manager;

        internal PredictedLagCompensation(PredictionManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// The PurrNet rollback module backing these queries, or null when collider rollback is
        /// unavailable for the manager's scene.
        /// </summary>
        public RollbackModule module => _manager ? _manager.rollbackModule : null;

        /// <summary>
        /// Resolves a prediction tick into the module and precise tick PurrNet's rollback API expects.
        /// Use this to call any RollbackModule method that has no wrapper here.
        /// </summary>
        public bool TryResolve(double predictionTick, out RollbackModule rollbackModule, out double rollbackTick)
        {
            rollbackModule = module;
            if (rollbackModule == null || !_manager.TryGetColliderRollbackTick(predictionTick, out rollbackTick))
            {
                rollbackTick = 0;
                return false;
            }

            return true;
        }

#if UNITY_PHYSICS_3D
        public bool Raycast(double predictionTick, Ray ray, out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.Raycast(tick, ray, out hit, maxDistance, layerMask, queryTriggers);
        }

        public int Raycast(double predictionTick, Ray ray, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.Raycast(tick, ray, raycastHits, maxDistance, layerMask, queryTriggers)
                : 0;
        }

        public bool SphereCast(double predictionTick, Ray ray, float radius, out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.SphereCast(tick, ray, radius, out hit, maxDistance, layerMask, queryTriggers);
        }

        public int SphereCast(double predictionTick, Ray ray, float radius, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.SphereCast(tick, ray, radius, raycastHits, maxDistance, layerMask, queryTriggers)
                : 0;
        }

        public bool BoxCast(double predictionTick, Ray ray, Vector3 halfExtents, Quaternion orientation,
            out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.BoxCast(tick, ray, halfExtents, orientation, out hit, maxDistance, layerMask, queryTriggers);
        }

        public int BoxCast(double predictionTick, Ray ray, Vector3 halfExtents, Quaternion orientation,
            RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.BoxCast(tick, ray, halfExtents, orientation, raycastHits, maxDistance, layerMask, queryTriggers)
                : 0;
        }

        public bool CapsuleCast(double predictionTick, Vector3 point1, Vector3 point2, Vector3 direction, float radius,
            out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.CapsuleCast(tick, point1, point2, direction, radius, out hit, maxDistance, layerMask, queryTriggers);
        }

        public int CapsuleCast(double predictionTick, Vector3 point1, Vector3 point2, Vector3 direction, float radius,
            RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CapsuleCast(tick, point1, point2, direction, radius, raycastHits, maxDistance, layerMask, queryTriggers)
                : 0;
        }

        public int SphereOverlap(double predictionTick, Vector3 origin, float radius, Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.SphereOverlap(tick, origin, radius, hits, layerMask, queryTriggers)
                : 0;
        }

        public bool CheckSphere(double predictionTick, Vector3 origin, float radius,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckSphere(tick, origin, radius, layerMask, queryTriggers);
        }

        public int BoxOverlap(double predictionTick, Vector3 origin, Vector3 halfExtents, Quaternion orientation,
            Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.BoxOverlap(tick, origin, halfExtents, orientation, hits, layerMask, queryTriggers)
                : 0;
        }

        public bool CheckBox(double predictionTick, Vector3 origin, Vector3 halfExtents, Quaternion orientation,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckBox(tick, origin, halfExtents, orientation, layerMask, queryTriggers);
        }

        public int CapsuleOverlap(double predictionTick, Vector3 point1, Vector3 point2, float radius,
            Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CapsuleOverlap(tick, point1, point2, radius, hits, layerMask, queryTriggers)
                : 0;
        }

        public bool CheckCapsule(double predictionTick, Vector3 point1, Vector3 point2, float radius,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckCapsule(tick, point1, point2, radius, layerMask, queryTriggers);
        }
#endif

#if UNITY_PHYSICS_2D
        public bool Raycast(double predictionTick, Ray2D ray, out RaycastHit2D hit,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.Raycast(tick, ray, out hit, maxDistance, contactFilter);
        }

        public int Raycast(double predictionTick, Ray2D ray, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.Raycast(tick, ray, raycastHits, maxDistance, contactFilter)
                : 0;
        }

        public bool CircleCast(double predictionTick, Ray2D ray, float radius, out RaycastHit2D hit,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.CircleCast(tick, ray, radius, out hit, maxDistance, contactFilter);
        }

        public int CircleCast(double predictionTick, Ray2D ray, float radius, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CircleCast(tick, ray, radius, raycastHits, maxDistance, contactFilter)
                : 0;
        }

        public bool BoxCast(double predictionTick, Ray2D ray, Vector2 size, float angle, out RaycastHit2D hit,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.BoxCast(tick, ray, size, angle, out hit, maxDistance, contactFilter);
        }

        public int BoxCast(double predictionTick, Ray2D ray, Vector2 size, float angle, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.BoxCast(tick, ray, size, angle, raycastHits, maxDistance, contactFilter)
                : 0;
        }

        public bool CapsuleCast(double predictionTick, Ray2D ray, Vector2 size, CapsuleDirection2D capsuleDirection,
            float angle, out RaycastHit2D hit,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            return module.CapsuleCast(tick, ray, size, capsuleDirection, angle, out hit, maxDistance, contactFilter);
        }

        public int CapsuleCast(double predictionTick, Ray2D ray, Vector2 size, CapsuleDirection2D capsuleDirection,
            float angle, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CapsuleCast(tick, ray, size, capsuleDirection, angle, raycastHits, maxDistance, contactFilter)
                : 0;
        }

        public int CircleOverlap(double predictionTick, Vector2 origin, float radius, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CircleOverlap(tick, origin, radius, hits, contactFilter)
                : 0;
        }

        public bool CheckCircle(double predictionTick, Vector2 origin, float radius,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckCircle(tick, origin, radius, contactFilter);
        }

        public int BoxOverlap(double predictionTick, Vector2 origin, Vector2 size, float angle, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.BoxOverlap(tick, origin, size, angle, hits, contactFilter)
                : 0;
        }

        public bool CheckBox(double predictionTick, Vector2 origin, Vector2 size, float angle,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckBox(tick, origin, size, angle, contactFilter);
        }

        public int CapsuleOverlap(double predictionTick, Vector2 origin, Vector2 size,
            CapsuleDirection2D capsuleDirection, float angle, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick)
                ? module.CapsuleOverlap(tick, origin, size, capsuleDirection, angle, hits, contactFilter)
                : 0;
        }

        public bool CheckCapsule(double predictionTick, Vector2 origin, Vector2 size,
            CapsuleDirection2D capsuleDirection, float angle,
            ContactFilter2D contactFilter = default)
        {
            return TryResolve(predictionTick, out var module, out var tick) &&
                   module.CheckCapsule(tick, origin, size, capsuleDirection, angle, contactFilter);
        }
#endif
    }
}
