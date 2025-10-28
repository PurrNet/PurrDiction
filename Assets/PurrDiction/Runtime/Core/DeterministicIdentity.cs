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

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            if (predictionManager.validateDeterministicData)
            {
                Packer<bool>.Write(packer, true);
                Packer<STATE>.Write(packer, fullPredictedState.state);
                return true;
            }

            return false;
        }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            if (predictionManager.validateDeterministicData)
            {
                packer.AdvanceBits(1);
                STATE read = default;
                Packer<STATE>.Read(packer, ref read);

                if (!Packer.AreEqual(read, fullPredictedState.state))
                {
                    Debug.LogError(
                        $"State mismatch (tick: {predictionManager.time.tick}), should be:\n{read.ToString()}\nBut its:\n{fullPredictedState.state.ToString()}");
                    Debug.Break();
                }

                read.Dispose();
            }
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

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState);
        }

        public override void ResetState()
        {
            base.ResetState();
            ResetInterpolation();
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

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            myType = GetType();
            hierarchy = world.hierarchy;

            if (!isFreshSpawn)
            {
                fullPredictedState.state = GetInitialState();
                GetLatestUnityState();
                base.Setup(manager, world, id, owner);
                return;
            }

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            fullPredictedState.state = GetInitialState();
            GetLatestUnityState();

            var copy = fullPredictedState.DeepCopy();

            // if TickRate is 30, then this should be 2
            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);

            if (_interpolatedState == null)
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(FULLInterpolate, 1f / world.tickRate, copy, interpolationBuffer);
            else _interpolatedState.Teleport(copy);

            if (_stateHistory == null)
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();
            _stateHistory.Write(0, copy);
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected virtual void GetUnityState(ref STATE state) {}

        internal override void GetLatestUnityState()
        {
            fullPredictedState.prediction.owner = owner;
            // fullPredictedState.prediction.predictedID = id;
            GetUnityState(ref fullPredictedState.state);
        }

        /// <summary>
        /// Called before the first Simulate is executed
        /// </summary>
        protected virtual void SimulationStart() {}

        internal override void SimulateTick(ulong tick, float delta)
        {
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }

            Simulate(ref fullPredictedState.state, sfloat.FromFloat(delta));
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, sfloat.FromFloat(delta));

        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            owner = fullPredictedState.prediction.owner;
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            if (!_stateHistory.TryGet(tick, out var state))
            {
                PurrLogger.LogError($"Failed to write first state for tick {tick}");
                return;
            }

            Packer<PredictedIdentityState>.Write(packer, state.prediction);
            Packer<STATE>.Write(packer, state.state);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);

            _stateHistory.Write(tick, new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            });
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable) { }

        internal override void ReadInput(ulong tick,  PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable) { }

        public STATE viewState;

        public STATE? verifiedState => _stateHistory.Count > 0 ? _stateHistory[^1].state : null;

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
            UpdateView(viewState, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);
        }

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

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
