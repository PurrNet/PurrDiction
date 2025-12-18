using System;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        protected readonly struct DeltaKey<T, S> : IStableHashable
        {
            private readonly PredictedComponentID id;
            private readonly SceneID scene;

            public DeltaKey(SceneID scene, PredictedComponentID id)
            {
                this.id = id;
                this.scene = scene;
            }

            public uint GetStableHash()
            {
                const uint Off = 2166136261u;
                const uint Pri = 16777619u;
                uint h = Off;
                h = (h ^ Hasher<T>.stableHash) * Pri;
                h = (h ^ Hasher<S>.stableHash) * Pri;
                h = (h ^ id.componentId.value) * Pri;
                h = (h ^ id.objectId.instanceId.value) * Pri;
                h = (h ^ scene.id.value) * Pri;
                return h;
            }
        }

        protected readonly struct DeltaKey<T> : IStableHashable
        {
            private readonly PredictedComponentID id;
            private readonly SceneID scene;

            public DeltaKey(SceneID scene, PredictedComponentID id)
            {
                this.id = id;
                this.scene = scene;
            }

            public uint GetStableHash()
            {
                const uint Off = 2166136261u;
                const uint Pri = 16777619u;
                uint h = Off;
                h = (h ^ Hasher<T>.stableHash) * Pri;
                h = (h ^ id.componentId.value) * Pri;
                h = (h ^ id.objectId.instanceId.value) * Pri;
                h = (h ^ scene.id.value) * Pri;
                return h;
            }
        }

        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
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

        protected Type myType;

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

            // if TickRate is 30, then this should be 2
            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);

            if (_interpolatedState == null)
            {
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(
                    FULLInterpolate, 1f / world.tickRate, fullPredictedState.DeepCopy(), interpolationBuffer);
            }
            else _interpolatedState.Teleport(fullPredictedState.DeepCopy());

            if (_stateHistory == null)
                 _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();

            _stateHistory.Write(0, fullPredictedState.DeepCopy());
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
            Simulate(ref fullPredictedState.state, delta);
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, delta);

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

        protected virtual void Simulate(ref STATE state, float delta) {}

        protected virtual void LateSimulate(ref STATE state, float delta) {}

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

        protected DeltaKey<STATE> stateKey => new (sceneId, id);

        private DeltaKey<PredictedIdentityState, STATE> internalKey => new (sceneId, id);

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _stateHistory.TryGet(tick, out var state))
                savedState = state;

            Packer<PredictedIdentityState>.Write(packer, savedState.prediction);
            Packer<STATE>.Write(packer, savedState.state);
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

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            int flagPos = packer.AdvanceBits(1);

            bool changed = deltaModule.WriteReliable(packer, target, internalKey, fullPredictedState.prediction);
            changed = WriteDeltaState(target, packer, deltaModule) || changed;

            packer.WriteAt(flagPos, changed);
            if (!changed)
                packer.SetBitPosition(flagPos + 1);

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return changed;
        }

        protected virtual bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            return deltaModule.WriteReliable(packer, target, stateKey, fullPredictedState.state);
        }

        [UsedImplicitly]
        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;

            bool changed = Packer<bool>.Read(packer);
            if (changed)
            {
                STATE state = default;
                PredictedIdentityState prediction = default;

                deltaModule.ReadReliable(packer, internalKey, ref prediction);
                ReadDeltaState(packer, deltaModule, ref state);

                _stateHistory.Write(tick, new FULL_STATE<STATE>
                {
                    state = state,
                    prediction = prediction
                });
            }
            else
            {
                packer.SetBitPosition(pos);

                STATE state = default;
                PredictedIdentityState prediction = default;

                deltaModule.ReadReliable(packer, internalKey, ref prediction);
                packer.SetBitPosition(pos);
                ReadDeltaState(packer, deltaModule, ref state);

                _stateHistory.Write(tick, new FULL_STATE<STATE>
                {
                    state = state,
                    prediction = prediction
                });
            }

            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        protected virtual void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, ref STATE state)
        {
            deltaModule.ReadReliable(packer, stateKey, ref state);
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable) { }

        internal override void ReadInput(ulong tick,  PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable) { }

        public STATE viewState;

        public STATE? verifiedState
        {
            get
            {
                if (lastVerifiedTick.HasValue && _stateHistory.TryGet(lastVerifiedTick.Value, out var state))
                    return state.state;
                return null;
            }
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
            UpdateView(viewState, verifiedState);
        }

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

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
