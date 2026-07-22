using System;
using System.Runtime.CompilerServices;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        /// <summary>
        /// Override when OnVerifiedStateReceived accumulates a smooth correction for this module.
        /// Modules that do not opt in snap their live state to the verified value when their parent
        /// identity uses SoftCorrection, rather than silently ignoring authoritative state.
        /// </summary>
        public virtual bool supportsSoftCorrection => false;

        internal MODULE_STATE<TState> fullPredictedState;

        /// <summary>
        /// The current simulation relevant state
        /// </summary>
        public ref TState currentState => ref fullPredictedState.state;

        private History<MODULE_STATE<TState>> _history;

        private InterpolatedWithDispose<MODULE_STATE<TState>> _interpolatedState;
        private MODULE_STATE<TState>? _viewState;
        private float _baseInterpolationTickDelta;
        private int _defaultInterpolationBufferSize;
        private ulong? _lastSparseInterpolationTick;

        public TState viewState;

        /// <summary>
        /// The last fully verified state received from the server (or authoritative state if local).
        /// Returns null if no history exists yet.
        /// </summary>
        public TState? verifiedState
        {
            get
            {
                if (identity.lastVerifiedTick.HasValue &&
                    _history != null &&
                    _history.ReadOrPrevious(identity.lastVerifiedTick.Value, out var state))
                {
                    return state.state;
                }

                return null;
            }
        }

        public PredictedModule(PredictedIdentity identity) : base(identity) { }

        /// <summary>
        /// Stable key retained for source compatibility with custom predicted modules.
        /// </summary>
        protected ModuleDeltaKey<PredictedIdentityState> predictionKey => new ModuleDeltaKey<PredictedIdentityState>(identity.id, moduleIndex);

        private ModuleDeltaKey<ModulePredictedState> modulePredictionKey => new ModuleDeltaKey<ModulePredictedState>(identity.id, moduleIndex);
        protected ModuleDeltaKey<TState> stateKey => new ModuleDeltaKey<TState>(identity.id, moduleIndex);

        public override string ToString()
        {
            return $"State:\n{fullPredictedState.state}";
        }

        private void ResetStateToInitialState()
        {
            fullPredictedState.prediction.wasOnSimulationStartCalled = false;
            fullPredictedState.state.Dispose();
            fullPredictedState.state = GetInitialState();
        }

        internal override void OnCoreInitialize()
        {
            var tickRate = predictionManager.tickRate;
            var bufferSize = (int)Math.Max(tickRate / 10f, 2);
            _baseInterpolationTickDelta = 1f / tickRate;
            _defaultInterpolationBufferSize = bufferSize;

            _history = new History<MODULE_STATE<TState>>(tickRate * 10);

            _interpolatedState = new InterpolatedWithDispose<MODULE_STATE<TState>>(
                FULLInterpolate,
                _baseInterpolationTickDelta,
                fullPredictedState.DeepCopy(),
                Math.Max(bufferSize, identity.interestSendIntervalTicks)
            );

            SetInterestInterpolationWindowInternal(
                identity.interestSendIntervalTicks,
                identity.UsesSparseInterestInterpolation());
        }

        private History<MODULE_STATE<TState>> _verifiedHistory;
        private int _verifiedHistoryIndex = -1;

        private History<MODULE_STATE<TState>> verifiedHistory
        {
            get
            {
                if (_verifiedHistory == null || _verifiedHistoryIndex != moduleIndex)
                {
                    _verifiedHistory = predictionManager.GetVerifiedHistory<MODULE_STATE<TState>>(identity.id, 1 + moduleIndex, out _);
                    _verifiedHistoryIndex = moduleIndex;
                }

                return _verifiedHistory;
            }
        }

        private void RefreshVerifiedFromLive(ulong tick)
        {
            var store = verifiedHistory;
            if (store.Count > 0 && store.MostRecentTick >= tick)
                return;

            StoreVerified(tick, ref fullPredictedState);
        }

        private void StoreVerified(ulong serverTick, ref MODULE_STATE<TState> state)
        {
            var store = verifiedHistory;
            store.PruneByTickWindow(serverTick);

            int lastIndex = store.Count - 1;
            if (lastIndex >= 0 && store.GetEntryTick(lastIndex) <= serverTick)
            {
                var latest = store[lastIndex];
                if (latest.HasSameContents(ref state))
                    return;
            }

            store.Write(serverTick, state.DeepCopy());
        }

        protected override void Setup(PredictedIdentity parent, PredictionManager world)
        {
            base.Setup(parent, world);

            _verifiedHistory = null;
            _verifiedHistoryIndex = -1;

            bool preserveSoftCorrection = parent.preservesStateOnSetup &&
                                          parent.UsesSoftCorrectionTimeline() &&
                                          _interpolatedState != null &&
                                          _history != null;

            if (preserveSoftCorrection)
                return;

            ResetStateToInitialState();
            _history?.Clear();
            _viewState?.Dispose();
            _viewState = null;
            _lastSparseInterpolationTick = null;

            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }

        protected sealed override void UpdateView(float delta)
        {
            if (_interpolatedState == null) return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            var result = _interpolatedState.Advance(delta);
            viewState = result.state;

            UpdateView(viewState, verifiedState);
        }

        protected virtual void UpdateView(TState viewState, TState? verifiedState) { }

        /// <summary>
        /// Produces a transient, non-owning view state. Implementations must not allocate
        /// disposable members in the returned value.
        /// </summary>
        protected virtual TState Interpolate(TState from, TState to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        private MODULE_STATE<TState> FULLInterpolate(MODULE_STATE<TState> from, MODULE_STATE<TState> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new MODULE_STATE<TState>
            {
                state = state,
                prediction = from.prediction
            };
        }

        protected override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
            _lastSparseInterpolationTick = null;
        }

        protected override void UpdateInterpolation(float delta, bool accumulateError)
        {
            if (identity.UsesSparseInterestInterpolation())
            {
                if (!identity.lastVerifiedTick.HasValue ||
                    _lastSparseInterpolationTick == identity.lastVerifiedTick.Value)
                    return;

                _lastSparseInterpolationTick = identity.lastVerifiedTick.Value;
            }
            else
            {
                _lastSparseInterpolationTick = null;
            }

            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        internal override void SetInterestInterpolationWindowInternal(int sendIntervalTicks, bool sparse)
        {
            if (_interpolatedState == null)
                return;

            _interpolatedState.maxBufferSize = Math.Max(_defaultInterpolationBufferSize, sendIntervalTicks);
            _interpolatedState.tickDelta = sparse
                ? _baseInterpolationTickDelta * sendIntervalTicks
                : _baseInterpolationTickDelta;
            _lastSparseInterpolationTick = null;
        }

        protected virtual void ModifyRollbackViewState(ref TState state, float delta, bool accumulateError) { }

        protected override void Simulate(ulong tick, float delta)
        {
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }
            Simulate(ref fullPredictedState.state, delta);
        }

        protected virtual void SimulationStart() { }

        protected virtual TState GetInitialState() => default;

        protected virtual void Simulate(ref TState state, float delta) { }

        protected override void Rollback(ulong tick)
        {
            if (_history.ReadOrPrevious(tick, out var result))
            {
                fullPredictedState.Dispose();
                fullPredictedState = result.DeepCopy();
            }
        }

        protected override void SaveState(ulong tick)
        {
            if (LatestHistoryMatches(tick, ref fullPredictedState))
                return;

            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        private bool LatestHistoryMatches(ulong tick, ref MODULE_STATE<TState> state)
        {
            if (_history == null || _history.Count <= 0)
                return false;

            _history.PruneByTickWindow(tick);

            int lastIndex = _history.Count - 1;
            if (_history.GetEntryTick(lastIndex) > tick)
                return false;

            var last = _history[lastIndex];
            return last.HasSameContents(ref state);
        }

        private void WriteOwnedStateIfChanged(ulong tick, ref MODULE_STATE<TState> state)
        {
            if (LatestHistoryMatches(tick, ref state))
            {
                state.Dispose();
                state = default;
                return;
            }

            _history.Write(tick, state);
        }

        protected override bool WriteState(PlayerID receiver, BitPacker packer, ulong baselineTick)
        {
            RefreshVerifiedFromLive(predictionManager.localTick);
            var store = verifiedHistory;

            if (baselineTick > 0 && store.MostRecentTick <= baselineTick)
            {
                Packer<bool>.Write(packer, false);
                return false;
            }

            if (!store.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            Packer<bool>.Write(packer, true);
            DeltaPacker<ModulePredictedState>.Write(packer, baseline.prediction, fullPredictedState.prediction);
            DeltaPacker<TState>.Write(packer, baseline.state, fullPredictedState.state);
            return true;
        }

        protected override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            bool changed = Packer<bool>.Read(packer);

            if (!verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            MODULE_STATE<TState> newState;

            if (changed)
            {
                newState = default;
                DeltaPacker<ModulePredictedState>.Read(packer, baseline.prediction, ref newState.prediction);
                DeltaPacker<TState>.Read(packer, baseline.state, ref newState.state);
                StoreVerified(serverTick, ref newState);
            }
            else
            {
                newState = baseline.DeepCopy();
            }

            if (identity.UsesSoftCorrectionTimeline())
            {
                if (supportsSoftCorrection && _history.ReadOrPrevious(tick, out var predictedAtTick))
                {
                    OnVerifiedStateReceived(tick, in predictedAtTick.state, in newState.state);
                }
                else
                {
                    fullPredictedState.Dispose();
                    fullPredictedState = newState.DeepCopy();
                    ResetInterpolation();
                }

                WriteOwnedStateIfChanged(tick, ref newState);
                return;
            }

            fullPredictedState.Dispose();
            fullPredictedState = newState;
            if (!LatestHistoryMatches(tick, ref fullPredictedState))
                _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected virtual void OnVerifiedStateReceived(ulong tick, in TState predicted, in TState verified) { }

        protected override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _history.ReadOrPrevious(tick, out var historyState))
                savedState = historyState;

            var store = verifiedHistory;
            if (store.Count == 0 || store.MostRecentTick < tick)
                StoreVerified(tick, ref savedState);

            Packer<ModulePredictedState>.Write(packer, savedState.prediction);
            Packer<TState>.Write(packer, savedState.state);
        }

        protected override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            MODULE_STATE<TState> newState = default;
            Packer<ModulePredictedState>.Read(packer, ref newState.prediction);
            Packer<TState>.Read(packer, ref newState.state);
            StoreVerified(serverTick, ref newState);
            fullPredictedState.Dispose();
            fullPredictedState = newState;
            if (!LatestHistoryMatches(tick, ref fullPredictedState))
                _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override void ClearFuture(ulong tick)
        {
            _history.ClearFuture(tick);
        }

        protected override void ReleaseStateForPool()
        {
            DisposeStateStorage();
        }

        protected override void OnDisposed()
        {
            base.OnDisposed();
            DisposeStateStorage();
            _interpolatedState = null;
        }

        private void DisposeStateStorage()
        {
            _history?.Clear();
            _viewState?.Dispose();
            _viewState = null;
            _interpolatedState?.Teleport(default);
            _lastSparseInterpolationTick = null;
            _verifiedHistory = null;
            _verifiedHistoryIndex = -1;
            fullPredictedState.Dispose();
            fullPredictedState = default;
            viewState = default;
        }
    }
}
