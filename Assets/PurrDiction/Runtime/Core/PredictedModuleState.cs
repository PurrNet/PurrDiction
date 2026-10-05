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

        private PredictedViewBuffer<MODULE_STATE<TState>> _interpolatedState;
        private ulong _viewStateTick;

        private ulong viewTeleportTick => predictionManager ? predictionManager.localTickInContext : 0;
        private MODULE_STATE<TState>? _viewState;

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
            var bufferSize = PredictionManager.GetViewInterpolationMaxBufferSize(tickRate);

            _history = new History<MODULE_STATE<TState>>(tickRate * 10);

            _interpolatedState = new PredictedViewBuffer<MODULE_STATE<TState>>(
                FULLInterpolate,
                predictionManager.localTickInContext,
                fullPredictedState.DeepCopy(),
                bufferSize + 2
            );
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

        // One exact comparison per tick serves every receiver; see PredictedIdentity<T>.RefreshVerifiedFromLive.
        private ulong _liveVerifiedThroughTick;

        private void RefreshVerifiedFromLive(ulong tick)
        {
            var store = verifiedHistory;
            if (store.Count > 0 && store.MostRecentTick >= tick)
                return;

            if (_liveVerifiedThroughTick == tick)
                return;

            StoreVerified(tick, ref fullPredictedState);
            _liveVerifiedThroughTick = tick;
        }

        private void StoreVerified(ulong serverTick, ref MODULE_STATE<TState> state)
        {
            var store = verifiedHistory;
            store.PruneByTickWindow(serverTick);
            VerifiedStateStore<MODULE_STATE<TState>>.StoreLive(store, serverTick, ref state, true);
        }

        private void StoreReceivedVerified(ulong serverTick, ref MODULE_STATE<TState> state,
            ulong? unchangedBaselineTick = null)
        {
            var store = verifiedHistory;
            store.PruneByTickWindow(serverTick);
            VerifiedStateStore<MODULE_STATE<TState>>.StoreReceived(store, serverTick, ref state, unchangedBaselineTick, true);
        }

        protected override void Setup(PredictedIdentity parent, PredictionManager world)
        {
            base.Setup(parent, world);

            _verifiedHistory = null;
            _verifiedHistoryIndex = -1;
            _liveVerifiedThroughTick = 0;

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

            _interpolatedState?.Teleport(viewTeleportTick, fullPredictedState.DeepCopy());
        }

        protected sealed override void UpdateView(float delta)
        {
            if (_interpolatedState == null) return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewStateTick, _viewState.Value);
                _viewState = null;
            }

            viewState = _interpolatedState.Sample(predictionManager.viewTick).state;

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
            _interpolatedState?.Teleport(viewTeleportTick, fullPredictedState.DeepCopy());
        }

        protected override void UpdateInterpolation(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            bool refreshOnly = predictionManager && predictionManager.refreshViewLatchOnly;
            if (!PredictionManager.ShouldReplaceViewLatch(refreshOnly, _viewState.HasValue))
            {
                copy.Dispose();
                return;
            }

            _viewState?.Dispose();
            _viewState = copy;
            _viewStateTick = predictionManager ? predictionManager.localTick : 0;
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

        /// <summary>
        /// Baseline that entering (first) states are delta-compressed against. It must be the same on
        /// every peer and must never change, so never derive it from scene or runtime data; the
        /// default is <c>default(TState)</c>. Override with a constant the spawned state usually resembles
        /// to shrink spawns. A returned instance is disposed after writing; when reading, unchanged
        /// fields may be shared into the decoded state, so that copy is not disposed.
        /// </summary>
        protected virtual TState GetFirstStateBaseline() => default;

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

            if (baselineTick > 0 &&
                baseline.HasSameContents(ref fullPredictedState))
            {
                Packer<bool>.Write(packer, false);
                return false;
            }

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
            }
            else
            {
                newState = baseline.DeepCopy();
            }

            ApplyVerifiedState(tick, serverTick, ref newState, changed ? null : baselineTick);
        }

        private void ApplyVerifiedState(ulong tick, ulong serverTick, ref MODULE_STATE<TState> newState,
            ulong? unchangedBaselineTick = null)
        {
            StoreReceivedVerified(serverTick, ref newState, unchangedBaselineTick);

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

                _history.PruneByTickWindow(tick);
                _history.Write(tick, newState);
                return;
            }

            fullPredictedState.Dispose();
            fullPredictedState = newState;
            _history.PruneByTickWindow(tick);
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override bool HasUnchangedStateBaseline(ulong baselineTick)
            => verifiedHistory.ReadOrPrevious(baselineTick, out _);

        protected override void ReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (!verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
            {
                throw new InvalidOperationException(
                    $"Missing module state baseline at tick {baselineTick}.");
            }

            var newState = baseline.DeepCopy();
            ApplyVerifiedState(tick, serverTick, ref newState, baselineTick);
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

            var baseline = GetFirstStateBaseline();
            DeltaPacker<ModulePredictedState>.Write(packer, default, savedState.prediction);
            DeltaPacker<TState>.Write(packer, baseline, savedState.state);
            baseline.Dispose();
        }

        protected override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            MODULE_STATE<TState> newState = default;
            var baseline = GetFirstStateBaseline();
            DeltaPacker<ModulePredictedState>.Read(packer, default, ref newState.prediction);
            DeltaPacker<TState>.Read(packer, baseline, ref newState.state);
            StoreReceivedVerified(serverTick, ref newState);
            fullPredictedState.Dispose();
            fullPredictedState = newState;
            _history.PruneByTickWindow(tick);
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
            _interpolatedState?.Teleport(viewTeleportTick, default);
            _verifiedHistory = null;
            _verifiedHistoryIndex = -1;
            _liveVerifiedThroughTick = 0;
            fullPredictedState.Dispose();
            fullPredictedState = default;
            viewState = default;
        }
    }
}
