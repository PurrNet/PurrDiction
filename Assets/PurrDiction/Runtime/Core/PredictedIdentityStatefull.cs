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

        private PredictedViewBuffer<FULL_STATE<STATE>> _interpolatedState;
        private ulong _viewStateTick;
        private ulong _viewSpawnTick;

        private ulong viewTeleportTick => predictionManager ? predictionManager.localTickInContext : 0;
        private History<FULL_STATE<STATE>> _stateHistory;
        private History<FULL_STATE<STATE>> _verifiedHistory;

        protected TickManager tickModule { get; private set; }
        private bool _firstViewUpdate = true;

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
            _viewAwaitsReplaySample = false;

            _interpolatedState?.Teleport(viewTeleportTick, default);
            _stateHistory?.Clear();
            _verifiedHistory = null;
            _liveVerifiedThroughTick = 0;

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
            bool sameSpawn = this.id.Equals(id) || continuesSpawnOnSetup;
            bool preserveInterpolation = world.isReplaying && !isFreshSpawn && sameSpawn &&
                                         _viewSpawnTick == world.localTickInContext;

            myType = GetType();
            hierarchy = world.hierarchy;

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;
            _liveVerifiedThroughTick = 0;

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

            if (!HasViewBufferFor(world))
            {
                _interpolatedState?.Teleport(0, default);
                _interpolatedState = new PredictedViewBuffer<FULL_STATE<STATE>>(
                    FULLInterpolate, world.localTickInContext, fullPredictedState.DeepCopy(), ViewBufferCapacity(world));
                RestartedView(world);
            }
            else if (!preserveInterpolation)
            {
                _interpolatedState.Teleport(world.localTickInContext, fullPredictedState.DeepCopy());
                RestartedView(world);
            }

            _stateHistory?.Clear();
            if (!HasStateHistoryFor(world))
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);

            _stateHistory.Write(0, fullPredictedState.DeepCopy());

            _verifiedHistory = world.GetVerifiedHistory<FULL_STATE<STATE>>(id, out _);
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

            world.PrewarmVerifiedStore<FULL_STATE<STATE>>();
        }

        private void RestartedView(PredictionManager world)
        {
            _viewSpawnTick = world.localTickInContext;
            OnViewInterpolationReset();
            _viewState?.Dispose();
            _viewState = null;
            _viewAwaitsReplaySample = world.isReplaying;
        }

        private bool _viewAwaitsReplaySample;

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

        private void WriteOwnedAuthoritativeState(ulong tick, in FULL_STATE<STATE> state)
        {
            _stateHistory.PruneByTickWindow(tick);
            _stateHistory.Write(tick, state);
        }

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            bool refreshOnly = predictionManager && predictionManager.refreshViewLatchOnly;
            if (!PredictionManager.ShouldReplaceViewLatch(refreshOnly, _viewState.HasValue || _viewAwaitsReplaySample))
            {
                copy.Dispose();
                return;
            }

            _viewState?.Dispose();
            _viewState = copy;
            _viewStateTick = predictionManager ? predictionManager.localTick : 0;
            _viewAwaitsReplaySample = false;
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

            _interpolatedState.Teleport(viewTeleportTick, new FULL_STATE<STATE>
            {
                state = state,
                prediction = fullPredictedState.prediction
            });
        }

        protected virtual STATE GetInitialState() => default;

        /// <summary>
        /// Baseline that entering (first) states are delta-compressed against. It must be the same on
        /// every peer and must never change, so never derive it from scene or runtime data; the
        /// default is <c>default(STATE)</c>. Override with a constant the spawned state usually resembles
        /// to shrink spawns. A returned instance is disposed after writing; when reading, unchanged
        /// fields may be shared into the decoded state, so that copy is not disposed.
        /// </summary>
        protected virtual STATE GetFirstStateBaseline() => default;

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

            var baseline = GetFirstStateBaseline();
            DeltaPacker<PredictedIdentityState>.Write(packer, default, fullPredictedState.prediction);
            DeltaPacker<STATE>.Write(packer, baseline, fullPredictedState.state);
            baseline.Dispose();
        }

        // All receivers read the same pre-simulation value, so compare and store it once per tick.
        private ulong _liveVerifiedThroughTick;

        internal void RefreshVerifiedFromLive(ulong tick)
        {
            if (_verifiedHistory.Count > 0 && _verifiedHistory.MostRecentTick >= tick)
                return;

            if (_liveVerifiedThroughTick == tick)
                return;

            StoreVerified(tick, ref fullPredictedState);
            _liveVerifiedThroughTick = tick;
        }

        internal History<FULL_STATE<STATE>> verifiedStateHistory => _verifiedHistory;

        internal bool TryGetVerifiedState(
            ulong tick,
            out PredictedIdentityState prediction,
            out STATE state)
        {
            if (_verifiedHistory != null && _verifiedHistory.ReadOrPrevious(tick, out var fullState))
            {
                prediction = fullState.prediction;
                state = fullState.state;
                return true;
            }

            prediction = default;
            state = default;
            return false;
        }

        // Events belong to one tick; substituting an older batch would replay its callbacks.
        internal bool TryGetExactVerifiedState(ulong tick, out STATE state)
        {
            if (_verifiedHistory != null && _verifiedHistory.TryGet(tick, out var snapshot))
            {
                state = snapshot.state;
                return true;
            }

            state = default;
            return false;
        }

        internal bool RestoreVerifiedState(ulong tick)
        {
            if (_verifiedHistory == null ||
                !_verifiedHistory.ReadOrPrevious(tick, out var verified))
            {
                return false;
            }

            var copy = verified.DeepCopy();
            WriteOwnedAuthoritativeState(tick, copy);
            Rollback(tick);
            return true;
        }

        internal void WriteFirstProjectedState(ulong tick, BitPacker packer, in STATE projectedState)
        {
            RefreshVerifiedFromLive(tick);
            var baseline = GetFirstStateBaseline();
            DeltaPacker<PredictedIdentityState>.Write(packer, default, fullPredictedState.prediction);
            DeltaPacker<STATE>.Write(packer, baseline, projectedState);
            baseline.Dispose();
        }

        internal bool WriteProjectedState(
            BitPacker packer,
            in PredictedIdentityState baselinePrediction,
            in STATE projectedBaseline,
            in STATE projectedCurrent)
        {
            int pos = packer.positionInBits;
            int changedPosition = packer.AdvanceBits(1);

            bool changed = DeltaPacker<PredictedIdentityState>.Write(
                packer,
                baselinePrediction,
                fullPredictedState.prediction);
            changed |= DeltaPacker<STATE>.Write(packer, projectedBaseline, projectedCurrent);

            packer.WriteAt(changedPosition, changed);
            if (!changed)
                packer.SetBitPosition(changedPosition + 1);

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return changed;
        }

        internal void RunWriteFirstProjectedState(
            ulong tick,
            BitPacker packer,
            in STATE projectedState)
        {
            WriteFirstDynamicModuleSnapshot(tick, packer);
            WriteFirstStateModules(tick, packer);
            WriteFirstProjectedState(tick, packer, projectedState);
        }

        internal bool RunWriteProjectedState(
            PlayerID receiver,
            BitPacker packer,
            ulong baselineTick,
            in PredictedIdentityState baselinePrediction,
            in STATE projectedBaseline,
            in STATE projectedCurrent)
        {
            bool moduleSetChanged = WriteDynamicModuleSnapshot(
                receiver,
                packer,
                baselineTick);
            bool modulesChanged = WriteModules(receiver, packer, baselineTick);
            bool stateChanged = WriteProjectedState(
                packer,
                baselinePrediction,
                projectedBaseline,
                projectedCurrent);
            return moduleSetChanged || modulesChanged || stateChanged;
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            var baseline = GetFirstStateBaseline();
            DeltaPacker<PredictedIdentityState>.Read(packer, default, ref prediction);
            DeltaPacker<STATE>.Read(packer, baseline, ref state);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            StoreReceivedVerified(serverTick, ref newState);
            WriteOwnedAuthoritativeState(tick, newState);
        }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, ulong baselineTick)
        {
            RefreshVerifiedFromLive(predictionManager.localTick);
            int pos = packer.positionInBits;

            if (baselineTick > 0 && _verifiedHistory.MostRecentTick <= baselineTick)
            {
                Packer<bool>.Write(packer, false);
                TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
                return false;
            }

            if (!_verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            if (baselineTick > 0 &&
                baseline.HasSameContents(ref fullPredictedState))
            {
                Packer<bool>.Write(packer, false);
                TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
                return false;
            }

            Packer<bool>.Write(packer, true);
            DeltaPacker<PredictedIdentityState>.Write(packer, baseline.prediction, fullPredictedState.prediction);
            WriteDeltaState(packer, in baseline.state, in fullPredictedState.state);

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return true;
        }

        /// <summary>
        /// Serializes the current state as a delta against the receiver's acknowledged baseline.
        /// Must be a pure function of the two states: ReadDeltaState must reconstruct the exact
        /// current state from the written bits and the same baseline. Records whose state matches
        /// the baseline may be omitted from the frame entirely, in which case the receiver carries
        /// the baseline forward without invoking either method.
        /// </summary>
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
            }
            else
            {
                newState = baseline.DeepCopy();
            }

            ApplyVerifiedState(tick, serverTick, ref newState, changed ? null : baselineTick);
            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        private void ApplyVerifiedState(ulong tick, ulong serverTick, ref FULL_STATE<STATE> newState,
            ulong? unchangedBaselineTick = null)
        {
            StoreReceivedVerified(serverTick, ref newState, unchangedBaselineTick);

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

            // Ownership transfers to the replay history.
            WriteOwnedAuthoritativeState(tick, newState);
        }

        internal override bool HasUnchangedStateBaseline(ulong baselineTick)
            => _verifiedHistory != null &&
               _verifiedHistory.ReadOrPrevious(baselineTick, out _);

        internal override bool TryGetFirstVerifiedTick(out ulong tick)
        {
            if (_verifiedHistory != null && _verifiedHistory.Count > 0)
            {
                tick = _verifiedHistory.OldestTick;
                return true;
            }
            tick = 0;
            return false;
        }

        internal override void ReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (_verifiedHistory == null ||
                !_verifiedHistory.ReadOrPrevious(baselineTick, out var baseline))
            {
                throw new InvalidOperationException(
                    $"Missing verified state baseline at tick {baselineTick}.");
            }

            var newState = baseline.DeepCopy();
            ApplyVerifiedState(tick, serverTick, ref newState, baselineTick);
        }

        private void PruneVerifiedHistory(ulong serverTick)
        {
            _verifiedHistory.PruneByTickWindow(serverTick);
            if (isEventHandler && serverTick > predictionManager.verifiedHistoryWindowTicks)
            {
                // Retain the oldest usable baseline until event deltas require a full frame.
                _verifiedHistory.PruneBeforeBaseline(
                    serverTick - predictionManager.verifiedHistoryWindowTicks, int.MaxValue);
            }
        }

        // Full and delta recipients must share one exact baseline per tick.
        // Equal event lists still need separate ticks, including repeated Stay callbacks.
        private void StoreVerified(ulong serverTick, ref FULL_STATE<STATE> state)
        {
            PruneVerifiedHistory(serverTick);
            VerifiedStateStore<FULL_STATE<STATE>>.StoreLive(_verifiedHistory, serverTick, ref state, !isEventHandler);
        }

        private void StoreReceivedVerified(ulong serverTick, ref FULL_STATE<STATE> state,
            ulong? unchangedBaselineTick = null)
        {
            PruneVerifiedHistory(serverTick);
            VerifiedStateStore<FULL_STATE<STATE>>.StoreReceived(
                _verifiedHistory, serverTick, ref state, unchangedBaselineTick, !isEventHandler);
        }

        protected virtual void OnVerifiedStateReceived(ulong tick, in STATE predicted, in STATE verified) { }

        protected virtual void ReadDeltaState(BitPacker packer, in STATE baseline, ref STATE state)
        {
            DeltaPacker<STATE>.Read(packer, baseline, ref state);
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender) { }

        public STATE viewState;

        /// <summary>
        /// Number of view samples currently buffered ahead of the rendered pose, or -1 before
        /// the view buffer exists.
        /// </summary>
        public int viewInterpolationBufferSize => _interpolatedState?.Count ?? -1;

        /// <summary>
        /// Prediction tick of the newest view sample at or before the presented tick, or 0 before
        /// the view buffer exists.
        /// </summary>
        public ulong viewAnchorTick => _interpolatedState?.anchorTick ?? 0;

        /// <summary>
        /// Prediction tick of the next pending view sample, or 0 when the view is holding its
        /// newest state. A gap larger than one tick from <see cref="viewAnchorTick"/> means the view
        /// is gliding across ticks that were never latched.
        /// </summary>
        public ulong viewNextSampleTick =>
            _interpolatedState != null && _interpolatedState.TryGetNextTick(out var tick) ? tick : 0;

        /// <summary>
        /// True while a tick has produced a view sample that the next view pass has not consumed
        /// yet.
        /// </summary>
        public bool hasPendingViewLatch => _viewState.HasValue;

        /// <summary>
        /// Latest verified state stored for this identity's id, independent of whether this
        /// component instance has consumed it.
        /// </summary>
        public bool TryGetLatestVerifiedState(out ulong tick, out STATE state)
        {
            if (_verifiedHistory != null && _verifiedHistory.Count > 0)
            {
                tick = _verifiedHistory.MostRecentTick;
                if (_verifiedHistory.ReadOrPrevious(tick, out var full))
                {
                    state = full.state;
                    return true;
                }
            }

            tick = 0;
            state = default;
            return false;
        }

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
