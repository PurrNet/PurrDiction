using System;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class DeterministicIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        [Tooltip("How the server responds when this identity's deterministic state diverges on a client. Inherit uses the PredictionManager's global policy.")]
        [SerializeField] private DesyncPolicyOverride _desyncPolicy = DesyncPolicyOverride.Inherit;

        /// <summary>
        /// Per-identity override of the PredictionManager's global desync policy.
        /// Resolved once during Setup; changing it after the identity is set up has no effect.
        /// </summary>
        public DesyncPolicyOverride desyncPolicy
        {
            get => _desyncPolicy;
            set => _desyncPolicy = value;
        }

        public override bool isDeterministic => true;

        protected virtual void Simulate(ref STATE state, sfloat delta) { }

        protected virtual void LateSimulate(ref STATE state, sfloat delta) { }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, ulong baselineTick)
        {
            return WritePredictionMetadata(packer, baselineTick, in fullPredictedState.prediction);
        }

        internal override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            ReadPredictionMetadata(packer, baselineTick, serverTick, ref prediction);
            ApplyPredictionMetadataAtTick(tick, in prediction);
        }

        internal override bool HasUnchangedStateBaseline(ulong baselineTick)
            => HasPredictionMetadataBaseline(baselineTick);

        internal override void ReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (!TryGetPredictionMetadataBaseline(baselineTick, out var prediction))
            {
                throw new InvalidOperationException(
                    $"Missing deterministic metadata baseline at tick {baselineTick}.");
            }

            StoreVerifiedMetadata(serverTick, in prediction);
            ApplyPredictionMetadataAtTick(tick, in prediction);
        }

        private void ApplyPredictionMetadataAtTick(ulong tick, in PredictedIdentityState prediction)
        {
            _stateHistory.PruneByTickWindow(tick);

            if (_stateHistory.Find(tick, out int index))
            {
                var snapshot = _stateHistory[index];
                snapshot.prediction = prediction;
                _stateHistory[index] = snapshot;
            }
            else if (index > 0)
            {
                var verified = _stateHistory[index - 1].DeepCopy();
                verified.prediction = prediction;
                _stateHistory.Write(tick, verified);
            }
            else
            {
                fullPredictedState.prediction = prediction;
                SetOwner(prediction.owner);
            }
        }

        internal override bool TryGetDeterministicStateHash(ulong tick, out ushort hash)
        {
            hash = 0;
            if (_stateHistory == null || !_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
                return false;

            using var packer = BitPackerPool.Get();
            Packer<STATE>.Write(packer, stateAtTick.state);
            hash = DeterministicStateHash.Compute(tick, packer);
            return true;
        }

        internal override string GetDeterministicStateString(ulong tick)
        {
            if (_stateHistory == null || !_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
                return "<no state history at tick>";
            return stateAtTick.state.ToString();
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

        private PredictedViewBuffer<FULL_STATE<STATE>> _interpolatedState;
        private ulong _viewStateTick;

        private ulong viewTeleportTick => predictionManager ? predictionManager.localTickInContext : 0;
        private History<FULL_STATE<STATE>> _stateHistory;

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(viewTeleportTick, fullPredictedState.DeepCopy());
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

            _interpolatedState?.Teleport(viewTeleportTick, default);
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

            resolvedDesyncPolicy = DesyncPolicyResolution.Resolve(_desyncPolicy, world.desyncPolicy);

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            ResetStateToInitialState();
            GetLatestUnityState();

            if (!HasViewBufferFor(world))
            {
                _interpolatedState?.Teleport(0, default);
                _interpolatedState = new PredictedViewBuffer<FULL_STATE<STATE>>(
                    FULLInterpolate, world.localTickInContext, fullPredictedState.DeepCopy(), ViewBufferCapacity(world));
            }
            else
                _interpolatedState.Teleport(world.localTickInContext, fullPredictedState.DeepCopy());

            _viewState?.Dispose();
            _viewState = null;

            _stateHistory?.Clear();
            if (!HasStateHistoryFor(world))
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            _stateHistory.Write(0, fullPredictedState.DeepCopy());
        }

        private static int ViewBufferCapacity(PredictionManager world)
            => PredictionManager.GetViewInterpolationMaxBufferSize(world.tickRate) + 2;

        private bool HasViewBufferFor(PredictionManager world)
            => _interpolatedState != null && _interpolatedState.capacity == ViewBufferCapacity(world);

        private bool HasStateHistoryFor(PredictionManager world)
            => _stateHistory != null && _stateHistory.Capacity == world.tickRate * 10;

        internal override void PrewarmPredictionState(PredictionManager world)
        {
            base.PrewarmPredictionState(world);

            if (!HasViewBufferFor(world))
            {
                _interpolatedState?.Teleport(0, default);
                _interpolatedState = new PredictedViewBuffer<FULL_STATE<STATE>>(
                    FULLInterpolate, 0, default, ViewBufferCapacity(world));
            }

            if (!HasStateHistoryFor(world))
            {
                _stateHistory?.Clear();
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            }

            world.PrewarmVerifiedStore<PredictedIdentityState>();
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

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
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

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        /// <summary>
        /// Baseline that entering (first) states are delta-compressed against. It must be the same on
        /// every peer and must never change, so never derive it from scene or runtime data; the
        /// default is <c>default(STATE)</c>. Override with a constant the spawned state usually resembles
        /// to shrink spawns. A returned instance is disposed after writing; when reading, unchanged
        /// fields may be shared into the decoded state, so that copy is not disposed.
        /// </summary>
        protected virtual STATE GetFirstStateBaseline() => default;

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
            var baseline = GetFirstStateBaseline();
            DeltaPacker<PredictedIdentityState>.Write(packer, default, state.prediction);
            DeltaPacker<STATE>.Write(packer, baseline, state.state);
            baseline.Dispose();
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            var baseline = GetFirstStateBaseline();
            DeltaPacker<PredictedIdentityState>.Read(packer, default, ref prediction);
            DeltaPacker<STATE>.Read(packer, baseline, ref state);
            StoreVerifiedMetadata(serverTick, in prediction);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            _stateHistory.PruneByTickWindow(tick);
            _stateHistory.Write(tick, newState);
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
                _interpolatedState.Add(_viewStateTick, _viewState.Value);
                _viewState = null;
            }

            viewState = _interpolatedState.Sample(predictionManager.viewTick).state;

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
