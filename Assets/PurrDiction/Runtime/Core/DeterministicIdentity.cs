using System;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class DeterministicIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        public override bool isDeterministic => true;

        protected virtual void Simulate(ref STATE state, sfloat delta) { }

        protected virtual void LateSimulate(ref STATE state, sfloat delta) { }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, ulong baselineTick)
        {
            bool metadataChanged = WritePredictionMetadata(packer, baselineTick, in fullPredictedState.prediction);

            if (predictionManager.validateDeterministicData)
            {
                Packer<STATE>.Write(packer, fullPredictedState.state);
                return true;
            }

            return metadataChanged;
        }

        internal override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            ReadPredictionMetadata(packer, baselineTick, serverTick, ref prediction);

            if (predictionManager.validateDeterministicData)
            {
                STATE read = default;
                Packer<STATE>.Read(packer, ref read);

                if (_hasPendingValidation)
                    _pendingValidationState.Dispose();

                _pendingValidationState = read;
                _hasPendingValidation = true;
            }

            if (_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
            {
                var verified = stateAtTick.DeepCopy();
                verified.prediction = prediction;
                WriteOwnedStateIfChanged(tick, ref verified);
            }
            else
            {
                fullPredictedState.prediction = prediction;
                SetOwner(prediction.owner);
            }
        }

        private STATE _pendingValidationState;
        private bool _hasPendingValidation;

        internal override void ValidateDeterministicState(ulong serverTick)
        {
            if (!_hasPendingValidation)
                return;

            _hasPendingValidation = false;

            if (!Packer.AreEqual(_pendingValidationState, fullPredictedState.state))
            {
                Debug.LogError(
                    $"State mismatch (server tick: {serverTick}), should be:\n{_pendingValidationState.ToString()}\nBut its:\n{fullPredictedState.state.ToString()}");
                Debug.Break();
            }

            _pendingValidationState.Dispose();
            _pendingValidationState = default;
        }

        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        internal override void ClearFuture(ulong stateTick)
        {
            _stateHistory.ClearFuture(stateTick);
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;
        private int _defaultInterpolationBufferSize;

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
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
            if (_hasPendingValidation)
            {
                _pendingValidationState.Dispose();
                _pendingValidationState = default;
                _hasPendingValidation = false;
            }

            _viewState?.Dispose();
            _viewState = null;

            _interpolatedState?.Teleport(default);
            _stateHistory?.Clear();

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

        internal Type myType;

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
            myType = GetType();
            hierarchy = world.hierarchy;

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            ResetStateToInitialState();
            GetLatestUnityState();

            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);
            _defaultInterpolationBufferSize = interpolationBuffer;

            if (_interpolatedState == null)
            {
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(
                    FULLInterpolate,
                    1f / world.tickRate,
                    fullPredictedState.DeepCopy(),
                    Math.Max(interpolationBuffer, interestSendIntervalTicks));
            }
            else
                _interpolatedState.Teleport(fullPredictedState.DeepCopy());

            SyncInterestInterpolationWindow(interestSendIntervalTicks, false);

            _viewState?.Dispose();
            _viewState = null;

            if (_stateHistory == null)
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();
            _stateHistory.Write(0, fullPredictedState.DeepCopy());
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

                Simulate(ref fullPredictedState.state, sfloat.FromFloat(delta));
            }
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, sfloat.FromFloat(delta));

        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.PruneByTickWindow(tick);
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
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        internal override void SyncInterestInterpolationWindow(int sendIntervalTicks, bool sparse)
        {
            if (_interpolatedState != null)
                _interpolatedState.maxBufferSize = Math.Max(_defaultInterpolationBufferSize, sendIntervalTicks);
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            SetOwner(fullPredictedState.prediction.owner);
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
            {
                PurrLogger.LogError($"Failed to write first state for tick {tick}");
                return;
            }

            RefreshMetadataLedger(tick, in state.prediction);
            Packer<PredictedIdentityState>.Write(packer, state.prediction);
            Packer<STATE>.Write(packer, state.state);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);
            StoreVerifiedMetadata(serverTick, in prediction);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            WriteOwnedStateIfChanged(tick, ref newState);
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

        private bool _firstViewUpdate = true;

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

        protected virtual void ViewStart(STATE viewState, STATE? verified) {}

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

        protected virtual void LateUpdateView(STATE viewState, STATE? verified) {}

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

        public override void ReadFirstInput(ulong localTick, BitPacker packer) {}

        public override void WriteFirstInput(ulong localTick, BitPacker packer) {}
    }
}
