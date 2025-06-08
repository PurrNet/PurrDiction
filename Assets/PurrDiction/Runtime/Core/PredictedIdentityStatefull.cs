using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        public override string ToString()
        {
            return currentState.ToString();
        }

        internal struct FULL_STATE : IOptionalDispose
        {
            public STATE state;
            public PredictedIdentityState prediction;

            public FULL_STATE DeepCopy()
            {
                using var packer = BitPackerPool.Get();

                Packer<STATE>.Write(packer, state);
                Packer<PredictedIdentityState>.Write(packer, prediction);

                packer.ResetPositionAndMode(true);

                var data = new FULL_STATE();

                Packer<STATE>.Read(packer, ref data.state);
                Packer<PredictedIdentityState>.Read(packer, ref data.prediction);

                return data;
            }

            public void Dispose()
            {
                state.Dispose();
            }

            public override string ToString()
            {
                return $"{{state: {state}, prediction: {prediction}}}";
            }
        }

        private Interpolated<FULL_STATE> _interpolatedState;
        private History<FULL_STATE> _stateHistory;

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState.Teleport(fullPredictedState);
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick) { }

        private FULL_STATE FULLInterpolate(FULL_STATE from, FULL_STATE to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE
            {
                state = state,
                prediction = from.prediction
            };
        }

        internal FULL_STATE fullPredictedState;

        public STATE currentState
        {
            get => fullPredictedState.state;
            set => fullPredictedState.state = value;
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn)
            {
                fullPredictedState.state = GetInitialState();
                GetLatestUnityState();
                return;
            }

            base.Setup(manager, world, id);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            fullPredictedState.state = GetInitialState();
            GetLatestUnityState();

            var copy = fullPredictedState.DeepCopy();

            // if TickRate is 30, then this should be 2
            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);

            _interpolatedState = new Interpolated<FULL_STATE>(FULLInterpolate, 1f / world.tickRate, copy, interpolationBuffer);
            _stateHistory = new History<FULL_STATE>(world.tickRate * 5);
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
            fullPredictedState.prediction.predictedID = id;
            GetUnityState(ref fullPredictedState.state);
        }

        internal override void SimulateTick(ulong tick, float delta) => Simulate(ref fullPredictedState.state, delta);

        internal override void SimulateRemote(ulong tick, float delta) => Simulate(ref fullPredictedState.state, delta);

        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        FULL_STATE? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy().DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(ref STATE state, float delta) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to rollback to tick {tick}, state not found.");
                return;
            }

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            owner = fullPredictedState.prediction.owner;
            id = fullPredictedState.prediction.predictedID;
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        readonly struct DeltaKey<T> : IStableHashable
        {
            private readonly PredictedID id;

            public DeltaKey(PredictedID id)
            {
                this.id = id;
            }

            public uint GetStableHash()
            {
                return Hasher<T>.stableHash/* ^ id.value.value*/;
            }
        }

        DeltaKey<STATE> stateKey => new (id);

        DeltaKey<PredictedIdentityState> internalKey => new (id);

        internal override void WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            if (deltaModule != null)
            {
                var validation = predictionManager.validateDeltaCompression;
                deltaModule.Write(packer, target, stateKey, fullPredictedState.state, validation);
                deltaModule.Write(packer, target, internalKey, fullPredictedState.prediction, validation);
            }
            else
            {
                Packer<STATE>.Write(packer, fullPredictedState.state);
                Packer<PredictedIdentityState>.Write(packer, fullPredictedState.prediction);
            }
        }

        [UsedImplicitly]
        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            STATE state = default;
            PredictedIdentityState prediction = default;

            if (deltaModule != null)
            {
                deltaModule.Read(packer, stateKey, default, ref state);
                deltaModule.Read(packer, internalKey, default, ref prediction);
            }
            else
            {
                Packer<STATE>.Read(packer, ref state);
                Packer<PredictedIdentityState>.Read(packer, ref prediction);
            }

            _stateHistory.Write(tick, new FULL_STATE
            {
                state = state,
                prediction = prediction
            });
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule) { }

        internal override void ReadInput(ulong tick, BitPacker packer, DeltaModule deltaModule) { }

        internal override void QueueInput(PlayerID sender, BitPacker packer, DeltaModule deltaModule) { }

        internal override void UpdateView(float deltaTime)
        {
            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            UpdateView(_interpolatedState.Advance(deltaTime).state, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);
        }

        protected virtual void UpdateView(STATE interpolatedState, STATE? verified) {}

        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }
    }
}
