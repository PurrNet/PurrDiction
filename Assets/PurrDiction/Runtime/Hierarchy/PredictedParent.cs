using PurrNet.Logging;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Makes this piece's transform parent part of predicted state: reparenting it is
    /// rolled back, replayed and delivered to late joiners like any other state change.
    /// Pieces without this component always belong to their authored default parent as
    /// far as the simulation is concerned; their actual transform parent is free
    /// client-local decoration.
    /// </summary>
    public class PredictedParent : PredictedIdentity<PredictedParent.ParentState>
    {
        public struct ParentState : IPredictedData<ParentState>
        {
            public PredictedComponentID? parent;

            public void Dispose() { }

            public override string ToString()
            {
                return parent.HasValue ? parent.Value.ToString() : "root";
            }
        }

        private PredictedComponentID? _resolved;
        private bool _dirty = true;
        private PredictedComponentID? _pendingParent;
        private bool _hasPendingParent;

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            _dirty = true;
        }

        /// <summary>
        /// Forces the parent link to be re-resolved on the next capture. Only needed when
        /// a plain transform between this piece and its predicted ancestor is restructured
        /// without this piece's own parent changing.
        /// </summary>
        public void RefreshParentLink()
        {
            _dirty = true;
        }

        internal PredictedComponentID? resolvedParent => Resolve();

        private PredictedComponentID? Resolve()
        {
            if (!_dirty && _resolved.HasValue)
            {
                if (predictionManager && predictionManager.TryGetIdentity(_resolved.Value, out var cached) && cached)
                    return _resolved;
                _dirty = true;
            }

            if (!_dirty)
                return _resolved;

            _resolved = null;
            var manager = predictionManager;

            if (manager && manager.hierarchy && manager.hierarchy.TryResolveParentLink(gameObject, out var link))
                _resolved = link;

            _dirty = false;
            return _resolved;
        }

        protected override ParentState GetInitialState()
        {
            _dirty = true;
            return new ParentState { parent = Resolve() };
        }

        protected override void GetUnityState(ref ParentState state)
        {
            if (_hasPendingParent)
            {
                var manager = predictionManager;

                if (manager && manager.hierarchy && manager.hierarchy.TryRestoreAttach(id.objectId, _pendingParent))
                {
                    _hasPendingParent = false;
                    _dirty = true;
                }
                else
                {
                    state.parent = _pendingParent;
                    return;
                }
            }

            state.parent = Resolve();
        }

        protected override void SetUnityState(ParentState state)
        {
            var current = Resolve();

            if (PredictedHierarchy.SameAttach(current, state.parent))
            {
                _hasPendingParent = false;
                return;
            }

            var manager = predictionManager;

            if (manager && manager.hierarchy && manager.hierarchy.TryRestoreAttach(id.objectId, state.parent))
            {
                _hasPendingParent = false;
                _dirty = true;
                return;
            }

            if ((!_hasPendingParent || !PredictedHierarchy.SameAttach(_pendingParent, state.parent)) &&
                (!state.parent.HasValue || !manager || !manager.hierarchy ||
                 !manager.hierarchy.IsUnavailableDueToLocalInterest(state.parent.Value)))
                PurrLogger.LogWarning($"'{name}' could not reattach to {(state.parent.HasValue ? state.parent.Value.ToString() : "root")}; keeping the target pending until it resolves.", this);

            _pendingParent = state.parent;
            _hasPendingParent = true;
        }
    }
}
