using System;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PredictedID : IPackedAuto, IEquatable<PredictedID>
    {
        public readonly PackedUInt value;

        public PredictedIdentity GetIdentity(PredictionManager manager)
        {
            return manager.GetIdentity(value);
        }

        public T GetIdentity<T>(PredictionManager manager) where T : PredictedIdentity
        {
            return (T)manager.GetIdentity(value);
        }

        public bool TryGetIdentity(PredictionManager manager, out PredictedIdentity identity)
        {
            identity = manager.GetIdentity(value);
            return identity != null;
        }

        public bool TryGetIdentity<T>(PredictionManager manager, out T identity) where T : PredictedIdentity
        {
            identity = (T)manager.GetIdentity(value);
            return identity != null;
        }

        public PredictedID(uint id)
        {
            this.value = id;
        }

        public bool Equals(PredictedID other)
        {
            return value == other.value;
        }

        public override bool Equals(object obj)
        {
            return obj is PredictedID other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)value.value;
        }
    }

    public abstract class PredictedIdentity : MonoBehaviour
    {
        public PredictionManager predictionManager { get; protected set; }

        public PlayerID? owner;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        public PredictedID id;

        internal bool isFreshSpawn = true;

        internal virtual bool isEventHandler => false;

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        protected virtual void OnSpawned() {}
        protected virtual void OnDespawned() {}

        internal virtual void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn)
                return;

            isFreshSpawn = false;

            owner = null;
            this.id = new PredictedID(id);
            predictionManager = world;

            OnSpawned();
        }

        protected virtual void OnDestroy()
        {
            OnDespawned();

            if (predictionManager)
                predictionManager.UnregisterInstance(this);
        }

        public bool IsOwner()
        {
            if (!predictionManager)
                return false;

            return owner == predictionManager.localPlayer;
        }

        public bool IsOwner(PlayerID player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID? player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID player, bool asServer)
        {
            if (owner.HasValue)
                return owner == player;
            return asServer;
        }

        internal abstract void SimulateTick(ulong tick, float delta);

        public virtual void PostSimulate(ulong tick, float delta) {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick);

        internal abstract void SimulateRemote(ulong tick, float delta);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        internal abstract void ResetInterpolation();

        internal abstract void UpdateView(float deltaTime);

        internal abstract void GetLatestUnityState();

        public abstract void WriteCurrentState(BitPacker packer);

        public abstract void WriteInput(ulong localTick, BitPacker input);

        public abstract void ReadState(ulong tick, BitPacker packer);

        public abstract void ReadInput(ulong tick, BitPacker packer);

        public abstract void QueueInput(BitPacker packer);

        public abstract void ClearInput();

        public GameObject GetRoot()
        {
            // get the farthest root with a predicted identity
            var current = transform;

            while (current.parent != null)
            {
                if (current.parent.GetComponent<PredictedIdentity>() == null)
                    break;

                current = current.parent;
            }

            return current.gameObject;
        }
    }

    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
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

        internal override void ResetInterpolation()
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

        public delegate void ModifyStateDelegate(ref STATE state);

        public void ModifyState(ModifyStateDelegate modify)
        {
            modify(ref fullPredictedState.state);
        }

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

        public override void WriteCurrentState(BitPacker packer)
        {
            Packer<STATE>.Write(packer, fullPredictedState.state);
            Packer<PredictedIdentityState>.Write(packer, fullPredictedState.prediction);
        }

        [UsedImplicitly]
        public override void ReadState(ulong tick, BitPacker packer)
        {
            STATE state = default;
            PredictedIdentityState prediction = default;
            Packer<STATE>.Read(packer, ref state);
            Packer<PredictedIdentityState>.Read(packer, ref prediction);

            _stateHistory.Write(tick, new FULL_STATE
            {
                state = state,
                prediction = prediction
            });
        }

        public override void WriteInput(ulong localTick, BitPacker input) { }

        public override void ReadInput(ulong tick, BitPacker packer) { }

        public override void QueueInput(BitPacker packer) { }

        public override void ClearInput() { }

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
