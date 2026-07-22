using System;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;
        private History<FULL_STATE<STATE>> _verifiedHistory;
        private float _baseInterpolationTickDelta;
        private int _defaultInterpolationBufferSize;
        private ulong? _lastSparseInterpolationTick;

        protected TickManager tickModule { get; private set; }
        private bool _firstViewUpdate = true;


        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
            _lastSparseInterpolationTick = null;
        }

        public override void ResetState()
        {
            base.ResetState();
            DisposeStateStorage();
            _firstViewUpdate = true;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            DisposeStateStorage();
        }

        internal override void ReleasePredictionStateForPool()
        {
            base.ReleasePredictionStateForPool();
            DisposeStateStorage();
        }

        private void DisposeStateStorage()
        {
            _viewState?.Dispose();
            _viewState = null;

            _interpolatedState?.Teleport(default);
            _lastSparseInterpolationTick = null;
            _stateHistory?.Clear();
            _verifiedHistory = null;

            fullPredictedState.Dispose();
            fullPredictedState = default;
            viewState = default;
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate) { }

        private FULL_STATE<STATE> FULLInterpolate(FULL_STATE<STATE> from, FULL_STATE<STATE> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE<STATE>
            {
                state = state,
                prediction = from.prediction
            };
        }

        internal FULL_STATE<STATE> fullPredictedState;

        public ref STATE currentState
        {
            get => ref fullPredictedState.state;
        }

        protected Type myType;

        private void ResetStateToInitialState()
        {
            fullPredictedState.prediction.wasOnSimulationStartCalled = false;
            fullPredictedState.state.Dispose();
            fullPredictedState.state = GetInitialState();
        }

        protected override void OnOwnerAssigned(PlayerID? player)
        {
            fullPredictedState.prediction.owner = player;
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            bool preserveInterpolation = world.isReplaying && !isFreshSpawn && this.id.Equals(id);

            myType = GetType();
            hierarchy = world.hierarchy;

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            bool preserveSoftCorrection = preservesStateOnSetup &&
                                          UsesSoftCorrectionTimeline() &&
                                          _interpolatedState != null &&
                                          _stateHistory != null;

            if (preserveSoftCorrection)
            {
                fullPredictedState.prediction.owner = owner;
                _verifiedHistory = world.GetVerifiedHistory<FULL_STATE<STATE>>(id, out _);
                return;
            }

            ResetStateToInitialState();
            GetLatestUnityState();

            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);
            _baseInterpolationTickDelta = 1f / world.tickRate;
            _defaultInterpolationBufferSize = interpolationBuffer;

            if (_interpolatedState == null)
            {
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(
                    FULLInterpolate,
                    _baseInterpolationTickDelta,
                    fullPredictedState.DeepCopy(),
                    Math.Max(interpolationBuffer, interestSendIntervalTicks));
                OnViewInterpolationReset();
            }
            else if (!preserveInterpolation)
            {
                _interpolatedState.Teleport(fullPredictedState.DeepCopy());
                OnViewInterpolationReset();
            }

            SyncInterestInterpolationWindow(interestSendIntervalTicks, UsesSparseInterestInterpolation());

            _viewState?.Dispose();
            _viewState = null;

            if (_stateHistory == null)
                 _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();

            _stateHistory.Write(0, fullPredictedState.DeepCopy());

            _verifiedHistory = world.GetVerifiedHistory<FULL_STATE<STATE>>(id, out _);
        }

        protected virtual void GetUnityState(ref STATE state) {}

        internal override void GetLatestUnityState()
        {
            fullPredictedState.prediction.owner = owner;
            GetUnityState(ref fullPredictedState.state);
        }

        protected virtual void SimulationStart() {}

        internal override void SimulateTick(ulong tick, float delta)
        {
            using (simulateMarker.Auto())
            {
                if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
                {
                    SimulationStart();
                    fullPredictedState.prediction.wasOnSimulationStartCalled = true;
                }

                Simulate(ref fullPredictedState.state, delta);
            }
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, delta);

        internal override void SaveStateInHistory(ulong tick)
        {
            if (LatestHistoryMatches(tick, ref fullPredictedState))
                return;

            lastChangedStateTick = tick;
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        private bool LatestHistoryMatches(ulong tick, ref FULL_STATE<STATE> state)
        {
            if (_stateHistory == null || _stateHistory.Count <= 0)
                return false;

            _stateHistory.PruneByTickWindow(tick);

            int lastIndex = _stateHistory.Count - 1;
            if (_stateHistory.GetEntryTick(lastIndex) > tick)
                return false;

            var last = _stateHistory[lastIndex];
            return last.HasSameContents(ref state);
        }

        private void WriteOwnedStateIfChanged(ulong tick, ref FULL_STATE<STATE> state)
        {
            if (LatestHistoryMatches(tick, ref state))
            {
                state.Dispose();
                state = default;
                return;
            }

            _stateHistory.Write(tick, state);
        }

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            if (UsesSparseInterestInterpolation())
            {
                if (!lastVerifiedTick.HasValue || _lastSparseInterpolationTick == lastVerifiedTick.Value)
                    return;

                _lastSparseInterpolationTick = lastVerifiedTick.Value;
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

        internal override void SyncInterestInterpolationWindow(int sendIntervalTicks, bool sparse)
        {
            if (_interpolatedState == null)
                return;

            _interpolatedState.maxBufferSize = Math.Max(_defaultInterpolationBufferSize, sendIntervalTicks);
            _interpolatedState.tickDelta = sparse
                ? _baseInterpolationTickDelta * sendIntervalTicks
                : _baseInterpolationTickDelta;
            _lastSparseInterpolationTick = null;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        /// <summary>
        /// Called whenever setup restarts the view interpolation buffer from the current
        /// predicted state. Implementations that feed the buffer in a transformed space via
        /// <see cref="ModifyRollbackViewState"/> must reset that space here, since the buffer
        /// now holds the untransformed state.
        /// </summary>
        protected virtual void OnViewInterpolationReset() { }

        /// <summary>
        /// Clears the view interpolation buffer and restarts it from the given state.
        /// The given state is expressed in whatever space the implementation feeds to the
        /// buffer via <see cref="ModifyRollbackViewState"/>; ownership transfers to the buffer.
        /// </summary>
        protected void TeleportViewState(STATE state)
        {
            if (_interpolatedState == null)
            {
                state.Dispose();
                return;
            }

            _viewState?.Dispose();
            _viewState = null;

            _interpolatedState.Teleport(new FULL_STATE<STATE>
            {
                state = state,
                prediction = fullPredictedState.prediction
            });
        }

        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(ref STATE state, float delta) {}

        protected virtual void LateSimulate(ref STATE state, float delta) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            ApplyVerifiedPredictionMetadata(in fullPredictedState.prediction);
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        private void ApplyVerifiedPredictionMetadata(in PredictedIdentityState prediction)
        {
            SetOwner(prediction.owner);
        }

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            RefreshVerifiedFromLive(tick);

            Packer<PredictedIdentityState>.Write(packer, fullPredictedState.prediction);
            Packer<STATE>.Write(packer, fullPredictedState.state);
        }

        private void RefreshVerifiedFromLive(ulong tick)
        {
            if (_verifiedHistory.Count > 0 && _verifiedHistory.MostRecentTick >= tick)
                return;

            StoreVerified(tick, ref fullPredictedState);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            StoreVerified(serverTick, ref newState);
            WriteOwnedStateIfChanged(tick, ref newState);
        }

        internal override void WriteAbsoluteState(ulong tick, BitPacker packer)
        {
            RefreshVerifiedFromLive(tick);

            int pos = packer.positionInBits;
            FULL_STATE<STATE> baseline = default;
            DeltaPacker<PredictedIdentityState>.Write(packer, baseline.prediction, fullPredictedState.prediction);
            WriteDeltaState(packer, in baseline.state, in fullPredictedState.state);
            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
        }

        internal override void ReadAbsoluteState(ulong tick, BitPacker packer, ulong serverTick)
        {
            int pos = packer.positionInBits;
            FULL_STATE<STATE> baseline = default;
            FULL_STATE<STATE> newState = default;
            DeltaPacker<PredictedIdentityState>.Read(packer, baseline.prediction, ref newState.prediction);
            ReadDeltaState(packer, in baseline.state, ref newState.state);
            StoreVerified(serverTick, ref newState);
            WriteOwnedStateIfChanged(tick, ref newState);
            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, ulong baselineTick)
        {
            RefreshVerifiedFromLive(predictionManager.localTick);

            if (baselineTick > 0 && _verifiedHistory.MostRecentTick <= baselineTick)
            {
                Packer<bool>.Write(packer, false);
                TickBandwidthProfiler.OnWroteState(myType, 1, this);
                return false;
            }

            int pos = packer.positionInBits;

            if (!_verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            Packer<bool>.Write(packer, true);
            DeltaPacker<PredictedIdentityState>.Write(packer, baseline.prediction, fullPredictedState.prediction);
            WriteDeltaState(packer, in baseline.state, in fullPredictedState.state);

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return true;
        }

        protected virtual void WriteDeltaState(BitPacker packer, in STATE baseline, in STATE current)
        {
            DeltaPacker<STATE>.Write(packer, baseline, current);
        }

        [UsedImplicitly]
        internal override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            int pos = packer.positionInBits;

            bool changed = Packer<bool>.Read(packer);

            if (!_verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            FULL_STATE<STATE> newState;

            if (changed)
            {
                newState = default;
                DeltaPacker<PredictedIdentityState>.Read(packer, baseline.prediction, ref newState.prediction);
                ReadDeltaState(packer, in baseline.state, ref newState.state);
                StoreVerified(serverTick, ref newState);
            }
            else
            {
                newState = baseline.DeepCopy();
            }

            if (UsesSoftCorrectionTimeline())
            {
                if (_stateHistory.ReadOrPrevious(tick, out var predictedAtTick))
                {
                    OnVerifiedStateReceived(tick, in predictedAtTick.state, in newState.state);
                    ApplyVerifiedPredictionMetadata(in newState.prediction);
                }
                else
                {
                    fullPredictedState.Dispose();
                    fullPredictedState = newState.DeepCopy();
                    ApplyVerifiedPredictionMetadata(in fullPredictedState.prediction);
                    SetUnityState(fullPredictedState.state);
                    ResetInterpolation();
                }
            }

            WriteOwnedStateIfChanged(tick, ref newState);
            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        private void StoreVerified(ulong serverTick, ref FULL_STATE<STATE> state)
        {
            _verifiedHistory.PruneByTickWindow(serverTick);

            int lastIndex = _verifiedHistory.Count - 1;
            if (lastIndex >= 0 && _verifiedHistory.GetEntryTick(lastIndex) <= serverTick)
            {
                var latest = _verifiedHistory[lastIndex];
                if (latest.HasSameContents(ref state))
                    return;
            }

            _verifiedHistory.Write(serverTick, state.DeepCopy());
        }

        protected virtual void OnVerifiedStateReceived(ulong tick, in STATE predicted, in STATE verified) { }

        protected virtual void ReadDeltaState(BitPacker packer, in STATE baseline, ref STATE state)
        {
            DeltaPacker<STATE>.Read(packer, baseline, ref state);
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender) { }

        public STATE viewState;

        public STATE? verifiedState
        {
            get
            {
                if (lastVerifiedTick.HasValue && _stateHistory.ReadOrPrevious(lastVerifiedTick.Value, out var state))
                    return state.state;
                return null;
            }
        }

        internal override void LateUpdateView(float deltaTime)
        {
            LateUpdateView(viewState, verifiedState);
        }

        internal override void UpdateView(float deltaTime)
        {
            base.UpdateView(deltaTime);

            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            viewState = _interpolatedState.Advance(deltaTime).state;

            if (_firstViewUpdate)
            {
                ViewStart(viewState, verifiedState);
                _firstViewUpdate = false;
            }

            UpdateView(viewState, verifiedState);
        }

        protected virtual void LateUpdateView(STATE viewState, STATE? verified) {}

        protected virtual void ViewStart(STATE viewState, STATE? verified) {}

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

        /// <summary>
        /// Produces a transient, non-owning view state. Implementations must not allocate
        /// disposable members in the returned value.
        /// </summary>
        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        internal override void ClearFuture(ulong stateTick)
        {
            _stateHistory.ClearFuture(stateTick);
        }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) {}

        public override void WriteFirstInput(ulong localTick, BitPacker packer) {}
    }
}
