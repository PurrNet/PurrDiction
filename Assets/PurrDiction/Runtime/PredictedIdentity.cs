using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictionState : IOptionalDispose
    {
        public PlayerID? owner;
        
        public void Dispose() {}
    }
    
    public abstract class PredictedIdentity : MonoBehaviour
    {
        public PredictionManager predictionManager { get; protected set; }

        public PlayerID? owner;

        public abstract void Setup(NetworkManager manager, PredictionManager world);

        protected void OnEnable()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.RegisterInstance(this);
        }

        private void OnDisable()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.UnregisterInstance(this);
        }
        
        public bool IsOwner()
        {
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

        internal abstract void EvaluateAndRegisterLocalInput(ulong localTick);
        
        internal abstract void SimulateTick(ulong tick, Fix64 delta);
        
        internal abstract void SimulateLocal(Fix64 delta);

        internal abstract void SimulateRemote(Fix64 delta);
        
        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        internal abstract void UpdateInterpolationState();

        internal abstract void ResetInterpolation();
        
        internal abstract void UpdateView(float deltaTime);
        
        public abstract void WriteLatestState(BitPacker packer);
        
        public abstract void WriteState(ulong tick, BitPacker packer);
        
        public abstract void WriteInput(ulong localTick, BitPacker input);

        public abstract void ReadState(ulong tick, BitPacker packer);
        
        public abstract void ReadInput(ulong tick, BitPacker packer);
        
        public abstract void QueueInput(BitPacker packer);
    }
    
    public abstract class PredictedIdentity<STATE> : PredictedIdentity 
        where STATE : struct, IOptionalDispose
    {
        struct FULL_STATE : IPackedAuto, IOptionalDispose
        {
            public STATE state;
            public PredictionState prediction;
            
            public void Dispose()
            {
                state.Dispose();
                prediction.Dispose();
            }
        }
        
        [SerializeField] PredictionSettings _predictionSettings;
        
        protected PredictionSettings settings => _predictionSettings;
        
        private Interpolated<FULL_STATE> _interpolatedState;
        
        private History<FULL_STATE> _stateHistory;
        
        private bool _resetInterpolation;
        
        protected STATE predictedState;

        protected STATE? verifiedState;
        
        protected TickManager tickModule { get; private set; }

        internal override void ResetInterpolation()
        {
            _resetInterpolation = true;
        }

        private FULL_STATE FULLInterpolate(FULL_STATE from, FULL_STATE to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE
            {
                state = state,
                prediction = from.prediction
            };
        }
        
        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            owner = null;
            predictionManager = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            predictedState = GetCurrentState();
            
            var predicted = new FULL_STATE
            {
                state = predictedState,
                prediction = new PredictionState
                {
                    owner = owner
                }
            };
            
            _predictionSettings ??= new PredictionSettings();
            
            _interpolatedState = new Interpolated<FULL_STATE>(FULLInterpolate, (float)world.tickDelta, predicted, settings.maxInputBufferCount);
            _stateHistory = new History<FULL_STATE>(world.tickRate * settings.secondsToKeepInHistory);
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected abstract STATE GetCurrentState();

        internal override void EvaluateAndRegisterLocalInput(ulong localTick) { }

        internal override void SimulateTick(ulong tick, Fix64 delta) => Simulate(delta);

        internal override void SimulateLocal(Fix64 delta) => Simulate(delta);

        internal override void SimulateRemote(Fix64 delta) => Simulate(delta);

        FULL_STATE GetCurrentFullState()
        {
            return new FULL_STATE
            {
                state = GetCurrentState(),
                prediction = new PredictionState
                {
                    owner = owner
                }
            };
        }
        
        FULL_STATE? _lastState;
        
        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, GetCurrentFullState());
        }
        
        internal override void UpdateInterpolationState()
        {
            _lastState = GetCurrentFullState();
        }

        protected abstract void Simulate(Fix64 delta);

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to rollback to tick {tick}, state not found.");
                return;
            }
            
            verifiedState = state.state;
            RollbackInternal(state.prediction);
            Rollback(state.state);
        }
        
        private void RollbackInternal(PredictionState state)
        {
            owner = state.owner;
        }
        
        protected abstract void Rollback(STATE state);

        [UsedImplicitly]
        public override void WriteState(ulong tick, BitPacker packer)
        {
            if (_stateHistory.Read(tick, out var state))
            {
                Packer<STATE>.Write(packer, state.state);
                Packer<PredictionState>.Write(packer, state.prediction);
            }
            else PurrLogger.LogError($"Failed to write state at tick {tick}");
        }

        public override void WriteLatestState(BitPacker packer)
        {
            var state = GetCurrentFullState();
            Packer<STATE>.Write(packer, state.state);
            Packer<PredictionState>.Write(packer, state.prediction);
        }

        [UsedImplicitly]
        public override void ReadState(ulong tick, BitPacker packer)
        {
            STATE state = default;
            PredictionState prediction = default;
            Packer<STATE>.Read(packer, ref state);
            Packer<PredictionState>.Read(packer, ref prediction);
            
            _stateHistory.Write(tick, new FULL_STATE
            {
                state = state,
                prediction = prediction
            });
        }

        public override void WriteInput(ulong localTick, BitPacker input) { }

        public override void ReadInput(ulong tick, BitPacker packer) { }

        public override void QueueInput(BitPacker packer) { }

        internal override void UpdateView(float deltaTime)
        {
            if (_interpolatedState == null)
                return;

            if (_lastState != null)
            {
                if (_resetInterpolation)
                {
                    _resetInterpolation = false;
                    _interpolatedState.Teleport(_lastState.Value);
                }
                else _interpolatedState.Add(_lastState.Value);
                _lastState = null;
            }

            var interpolatedState = _interpolatedState.Advance(deltaTime);
            
            UpdateView(interpolatedState.state, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);
        }

        protected virtual void UpdateView(STATE predicted, STATE? verified) {}

        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            return to;
        }
    }
}
