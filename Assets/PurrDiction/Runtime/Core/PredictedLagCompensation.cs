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
    /// <remarks>
    /// A client shows objects it does not predict (<see cref="PredictionPolicy.ServerRelay"/>,
    /// including other players' objects under <see cref="PredictionPolicy.PredictedIfOwned"/>)
    /// a few ticks behind the rest of its view. On the server, a query made inside Simulate of
    /// an identity owned by that client rewinds those colliders further, to where that client
    /// showed them.
    /// </remarks>
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

        // Where to rewind the colliders the current Simulate's owner saw relayed, when that differs
        // from the view tick. The server rewinds its own history; a client rewinds its relayed
        // history, which is keyed by server tick (PredictionManager.relayedRollbackModule).
        private bool TryResolveRelayed(double predictionTick, out RollbackModule relayModule, out double rollbackTick,
            out PlayerID viewer)
        {
            relayModule = null;
            rollbackTick = 0;
            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out var relayTick, out viewer))
                return false;

            if (_manager.cachedIsServer)
            {
                relayModule = module;
                return relayModule != null && _manager.TryGetColliderRollbackTick(relayTick, out rollbackTick);
            }

            relayModule = _manager.relayedRollbackModule;
            rollbackTick = relayTick;
            return relayModule != null;
        }

        private static bool IsRelayedTo(Component collider, PlayerID viewer)
        {
            if (!collider)
                return false;
            var identity = collider.GetComponentInParent<PredictedIdentity>();
            return identity && identity.PresentsRelayedViewTo(viewer);
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

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.Raycast(tick, ray, out hit, maxDistance, layerMask, queryTriggers);

            int count = Raycast(predictionTick, ray, Scratch3D.hits, maxDistance, layerMask, queryTriggers);
            return Nearest(Scratch3D.hits, count, out hit);
        }

        public int Raycast(double predictionTick, Ray ray, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.Raycast(tick, ray, raycastHits, maxDistance, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedHits;
            int relayedCount = relayModule.Raycast(relayTick, ray, relayed, maxDistance, layerMask, queryTriggers);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
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

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.SphereCast(tick, ray, radius, out hit, maxDistance, layerMask, queryTriggers);

            int count = SphereCast(predictionTick, ray, radius, Scratch3D.hits, maxDistance, layerMask, queryTriggers);
            return Nearest(Scratch3D.hits, count, out hit);
        }

        public int SphereCast(double predictionTick, Ray ray, float radius, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.SphereCast(tick, ray, radius, raycastHits, maxDistance, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedHits;
            int relayedCount = relayModule.SphereCast(relayTick, ray, radius, relayed, maxDistance, layerMask, queryTriggers);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
        }

        public bool BoxCast(double predictionTick, Ray ray, Vector3 halfExtents, Quaternion orientation, out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.BoxCast(tick, ray, halfExtents, orientation, out hit, maxDistance, layerMask, queryTriggers);

            int count = BoxCast(predictionTick, ray, halfExtents, orientation, Scratch3D.hits, maxDistance, layerMask, queryTriggers);
            return Nearest(Scratch3D.hits, count, out hit);
        }

        public int BoxCast(double predictionTick, Ray ray, Vector3 halfExtents, Quaternion orientation, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.BoxCast(tick, ray, halfExtents, orientation, raycastHits, maxDistance, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedHits;
            int relayedCount = relayModule.BoxCast(relayTick, ray, halfExtents, orientation, relayed, maxDistance, layerMask, queryTriggers);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
        }

        public bool CapsuleCast(double predictionTick, Vector3 point1, Vector3 point2, Vector3 direction, float radius, out RaycastHit hit,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CapsuleCast(tick, point1, point2, direction, radius, out hit, maxDistance, layerMask, queryTriggers);

            int count = CapsuleCast(predictionTick, point1, point2, direction, radius, Scratch3D.hits, maxDistance, layerMask, queryTriggers);
            return Nearest(Scratch3D.hits, count, out hit);
        }

        public int CapsuleCast(double predictionTick, Vector3 point1, Vector3 point2, Vector3 direction, float radius, RaycastHit[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CapsuleCast(tick, point1, point2, direction, radius, raycastHits, maxDistance, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedHits;
            int relayedCount = relayModule.CapsuleCast(relayTick, point1, point2, direction, radius, relayed, maxDistance, layerMask, queryTriggers);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
        }

        public int SphereOverlap(double predictionTick, Vector3 origin, float radius, Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.SphereOverlap(tick, origin, radius, hits, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedColliders;
            int relayedCount = relayModule.SphereOverlap(relayTick, origin, radius, relayed, layerMask, queryTriggers);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckSphere(double predictionTick, Vector3 origin, float radius,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckSphere(tick, origin, radius, layerMask, queryTriggers);

            return SphereOverlap(predictionTick, origin, radius, Scratch3D.colliders, layerMask, queryTriggers) > 0;
        }

        public int BoxOverlap(double predictionTick, Vector3 origin, Vector3 halfExtents, Quaternion orientation, Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.BoxOverlap(tick, origin, halfExtents, orientation, hits, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedColliders;
            int relayedCount = relayModule.BoxOverlap(relayTick, origin, halfExtents, orientation, relayed, layerMask, queryTriggers);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckBox(double predictionTick, Vector3 origin, Vector3 halfExtents, Quaternion orientation,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckBox(tick, origin, halfExtents, orientation, layerMask, queryTriggers);

            return BoxOverlap(predictionTick, origin, halfExtents, orientation, Scratch3D.colliders, layerMask, queryTriggers) > 0;
        }

        public int CapsuleOverlap(double predictionTick, Vector3 point1, Vector3 point2, float radius, Collider[] hits,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CapsuleOverlap(tick, point1, point2, radius, hits, layerMask, queryTriggers);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch3D.relayedColliders;
            int relayedCount = relayModule.CapsuleOverlap(relayTick, point1, point2, radius, relayed, layerMask, queryTriggers);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckCapsule(double predictionTick, Vector3 point1, Vector3 point2, float radius,
            int layerMask = Physics.AllLayers,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.UseGlobal)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckCapsule(tick, point1, point2, radius, layerMask, queryTriggers);

            return CapsuleOverlap(predictionTick, point1, point2, radius, Scratch3D.colliders, layerMask, queryTriggers) > 0;
        }

        private static class Scratch3D
        {
            internal static readonly RaycastHit[] hits = new RaycastHit[1024];
            internal static readonly RaycastHit[] relayedHits = new RaycastHit[1024];
            internal static readonly Collider[] colliders = new Collider[1024];
            internal static readonly Collider[] relayedColliders = new Collider[1024];
            static Scratch3D() { }
        }

        // Hits on colliders the viewer saw relayed come from the relayed rewind, all others from
        // the view-tick rewind.
        private static int MergeRelayed(RaycastHit[] hits, int count, RaycastHit[] relayed, int relayedCount, PlayerID viewer)
        {
            int kept = 0;
            for (var i = 0; i < count; i++)
            {
                if (!IsRelayedTo(hits[i].collider, viewer))
                    hits[kept++] = hits[i];
            }

            for (var i = 0; i < relayedCount && kept < hits.Length; i++)
            {
                if (IsRelayedTo(relayed[i].collider, viewer))
                    hits[kept++] = relayed[i];
            }

            return kept;
        }

        private static int MergeRelayed(Collider[] hits, int count, Collider[] relayed, int relayedCount, PlayerID viewer)
        {
            int kept = 0;
            for (var i = 0; i < count; i++)
            {
                if (!IsRelayedTo(hits[i], viewer))
                    hits[kept++] = hits[i];
            }

            for (var i = 0; i < relayedCount && kept < hits.Length; i++)
            {
                if (IsRelayedTo(relayed[i], viewer))
                    hits[kept++] = relayed[i];
            }

            return kept;
        }

        private static bool Nearest(RaycastHit[] hits, int count, out RaycastHit hit)
        {
            if (count <= 0)
            {
                hit = default;
                return false;
            }

            hit = hits[0];
            for (var i = 1; i < count; i++)
            {
                if (hits[i].distance < hit.distance)
                    hit = hits[i];
            }

            return true;
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

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.Raycast(tick, ray, out hit, maxDistance, contactFilter);

            int count = Raycast(predictionTick, ray, Scratch2D.hits, maxDistance, contactFilter);
            return Nearest(Scratch2D.hits, count, out hit);
        }

        public int Raycast(double predictionTick, Ray2D ray, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.Raycast(tick, ray, raycastHits, maxDistance, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedHits;
            int relayedCount = relayModule.Raycast(relayTick, ray, relayed, maxDistance, contactFilter);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
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

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CircleCast(tick, ray, radius, out hit, maxDistance, contactFilter);

            int count = CircleCast(predictionTick, ray, radius, Scratch2D.hits, maxDistance, contactFilter);
            return Nearest(Scratch2D.hits, count, out hit);
        }

        public int CircleCast(double predictionTick, Ray2D ray, float radius, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CircleCast(tick, ray, radius, raycastHits, maxDistance, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedHits;
            int relayedCount = relayModule.CircleCast(relayTick, ray, radius, relayed, maxDistance, contactFilter);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
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

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.BoxCast(tick, ray, size, angle, out hit, maxDistance, contactFilter);

            int count = BoxCast(predictionTick, ray, size, angle, Scratch2D.hits, maxDistance, contactFilter);
            return Nearest(Scratch2D.hits, count, out hit);
        }

        public int BoxCast(double predictionTick, Ray2D ray, Vector2 size, float angle, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.BoxCast(tick, ray, size, angle, raycastHits, maxDistance, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedHits;
            int relayedCount = relayModule.BoxCast(relayTick, ray, size, angle, relayed, maxDistance, contactFilter);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
        }

        public bool CapsuleCast(double predictionTick, Ray2D ray, Vector2 size, CapsuleDirection2D capsuleDirection, float angle, out RaycastHit2D hit,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
            {
                hit = default;
                return false;
            }

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CapsuleCast(tick, ray, size, capsuleDirection, angle, out hit, maxDistance, contactFilter);

            int count = CapsuleCast(predictionTick, ray, size, capsuleDirection, angle, Scratch2D.hits, maxDistance, contactFilter);
            return Nearest(Scratch2D.hits, count, out hit);
        }

        public int CapsuleCast(double predictionTick, Ray2D ray, Vector2 size, CapsuleDirection2D capsuleDirection, float angle, RaycastHit2D[] raycastHits,
            float maxDistance = float.PositiveInfinity,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CapsuleCast(tick, ray, size, capsuleDirection, angle, raycastHits, maxDistance, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedHits;
            int relayedCount = relayModule.CapsuleCast(relayTick, ray, size, capsuleDirection, angle, relayed, maxDistance, contactFilter);
            return MergeRelayed(raycastHits, count, relayed, relayedCount, viewer);
        }

        public int CircleOverlap(double predictionTick, Vector2 origin, float radius, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CircleOverlap(tick, origin, radius, hits, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedColliders;
            int relayedCount = relayModule.CircleOverlap(relayTick, origin, radius, relayed, contactFilter);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckCircle(double predictionTick, Vector2 origin, float radius,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckCircle(tick, origin, radius, contactFilter);

            return CircleOverlap(predictionTick, origin, radius, Scratch2D.colliders, contactFilter) > 0;
        }

        public int BoxOverlap(double predictionTick, Vector2 origin, Vector2 size, float angle, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.BoxOverlap(tick, origin, size, angle, hits, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedColliders;
            int relayedCount = relayModule.BoxOverlap(relayTick, origin, size, angle, relayed, contactFilter);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckBox(double predictionTick, Vector2 origin, Vector2 size, float angle,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckBox(tick, origin, size, angle, contactFilter);

            return BoxOverlap(predictionTick, origin, size, angle, Scratch2D.colliders, contactFilter) > 0;
        }

        public int CapsuleOverlap(double predictionTick, Vector2 origin, Vector2 size, CapsuleDirection2D capsuleDirection, float angle, Collider2D[] hits,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return 0;

            int count = module.CapsuleOverlap(tick, origin, size, capsuleDirection, angle, hits, contactFilter);
            if (!TryResolveRelayed(predictionTick, out var relayModule, out var relayTick, out var viewer))
                return count;

            var relayed = Scratch2D.relayedColliders;
            int relayedCount = relayModule.CapsuleOverlap(relayTick, origin, size, capsuleDirection, angle, relayed, contactFilter);
            return MergeRelayed(hits, count, relayed, relayedCount, viewer);
        }

        public bool CheckCapsule(double predictionTick, Vector2 origin, Vector2 size, CapsuleDirection2D capsuleDirection, float angle,
            ContactFilter2D contactFilter = default)
        {
            if (!TryResolve(predictionTick, out var module, out var tick))
                return false;

            if (!_manager.TryGetRelayedLagCompensationTick(predictionTick, out _, out _))
                return module.CheckCapsule(tick, origin, size, capsuleDirection, angle, contactFilter);

            return CapsuleOverlap(predictionTick, origin, size, capsuleDirection, angle, Scratch2D.colliders, contactFilter) > 0;
        }

        private static class Scratch2D
        {
            internal static readonly RaycastHit2D[] hits = new RaycastHit2D[1024];
            internal static readonly RaycastHit2D[] relayedHits = new RaycastHit2D[1024];
            internal static readonly Collider2D[] colliders = new Collider2D[1024];
            internal static readonly Collider2D[] relayedColliders = new Collider2D[1024];
            static Scratch2D() { }
        }

        // Hits on colliders the viewer saw relayed come from the relayed rewind, all others from
        // the view-tick rewind.
        private static int MergeRelayed(RaycastHit2D[] hits, int count, RaycastHit2D[] relayed, int relayedCount, PlayerID viewer)
        {
            int kept = 0;
            for (var i = 0; i < count; i++)
            {
                if (!IsRelayedTo(hits[i].collider, viewer))
                    hits[kept++] = hits[i];
            }

            for (var i = 0; i < relayedCount && kept < hits.Length; i++)
            {
                if (IsRelayedTo(relayed[i].collider, viewer))
                    hits[kept++] = relayed[i];
            }

            return kept;
        }

        private static int MergeRelayed(Collider2D[] hits, int count, Collider2D[] relayed, int relayedCount, PlayerID viewer)
        {
            int kept = 0;
            for (var i = 0; i < count; i++)
            {
                if (!IsRelayedTo(hits[i], viewer))
                    hits[kept++] = hits[i];
            }

            for (var i = 0; i < relayedCount && kept < hits.Length; i++)
            {
                if (IsRelayedTo(relayed[i], viewer))
                    hits[kept++] = relayed[i];
            }

            return kept;
        }

        private static bool Nearest(RaycastHit2D[] hits, int count, out RaycastHit2D hit)
        {
            if (count <= 0)
            {
                hit = default;
                return false;
            }

            hit = hits[0];
            for (var i = 1; i < count; i++)
            {
                if (hits[i].distance < hit.distance)
                    hit = hits[i];
            }

            return true;
        }
#endif
    }
}
