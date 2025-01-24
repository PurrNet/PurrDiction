using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity : MonoBehaviour
    {
        [SerializeField] protected bool _updateView = true;
        [SerializeField] protected int _maxInterpolationBuffer = 2;
        
        internal bool _isFreshSpawn = true;

        public PredictionManager predictionManager { get; protected set; }

        public PlayerID? owner;

        public abstract void Setup(NetworkManager manager, PredictionManager world);

        /*protected virtual void OnEnable()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.RegisterInstance(this);
        }*/

        protected virtual void OnDisable()
        {
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

        internal abstract void EvaluateAndRegisterLocalInput(ulong localTick);
        
        internal abstract void SimulateTick(ulong tick, Fix64 delta);
        
        internal abstract void SimulateLocal(Fix64 delta);

        internal abstract void SimulateRemote(ulong tick, Fix64 delta);
        
        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(Fix64 delta, bool accumulateError);

        internal abstract void ResetInterpolation();
        
        internal abstract void UpdateView(float deltaTime);

        internal abstract void GetLatestUnityState();
        
        public abstract void WriteLatestState(BitPacker packer);

        public abstract void WriteInput(ulong localTick, BitPacker input);

        public abstract void ReadState(ulong tick, BitPacker packer);
        
        public abstract void ReadInput(ulong tick, BitPacker packer);
        
        public abstract void QueueInput(BitPacker packer);

        public abstract void ClearInput();
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
        private bool _resetInterpolation = true;
        
        protected TickManager tickModule { get; private set; }

        internal override void ResetInterpolation()
        {
            _interpolatedState.Teleport(fullPredictedState);
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
        
        internal FULL_STATE fullPredictedState;
        
        public STATE currentState => fullPredictedState.state; 
        
        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            if (!_isFreshSpawn)
                return;
            
            owner = null;
            predictionManager = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            var initialState = GetInitialState();
            fullPredictedState.state = initialState;
            GetLatestUnityState();

            var copy = fullPredictedState.DeepCopy();
            
            _interpolatedState = new Interpolated<FULL_STATE>(FULLInterpolate, (float)world.tickDelta, copy, _maxInterpolationBuffer);
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
            GetUnityState(ref fullPredictedState.state);
        }

        internal override void EvaluateAndRegisterLocalInput(ulong localTick) { }

        internal override void SimulateTick(ulong tick, Fix64 delta) => Simulate(delta, ref fullPredictedState.state);

        internal override void SimulateLocal(Fix64 delta) => Simulate(delta, ref fullPredictedState.state);

        internal override void SimulateRemote(ulong tick, Fix64 delta) => Simulate(delta, ref fullPredictedState.state);

        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }
        
        FULL_STATE? _viewState;
        
        public override void UpdateRollbackInterpolationState(Fix64 delta, bool accumulateError)
        {
            if (!_updateView)
                return;
            
            _viewState?.Dispose();
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, Fix64 delta, bool accumulateError) { }
        
        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(Fix64 delta, ref STATE state) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to rollback to tick {tick}, state not found.");
                return;
            }
            
            owner = state.prediction.owner;
            
            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();
            
            RollbackInternal(fullPredictedState.prediction);
            SetUnityState(fullPredictedState.state);
            
            if (_isFreshSpawn)
            {
                _isFreshSpawn = false;
                _resetInterpolation = false;
                _interpolatedState.Teleport(fullPredictedState);
            }
        }
        
        private void RollbackInternal(PredictedIdentityState state)
        {
            owner = state.owner;
        }
        
        protected virtual void SetUnityState(STATE state) {}

        public virtual void WriteState(ulong tick, BitPacker packer)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to write state at tick {tick}");
                return;
            }
            
            Packer<STATE>.Write(packer, state.state);
            Packer<PredictedIdentityState>.Write(packer, state.prediction);
        }

        public override void WriteLatestState(BitPacker packer)
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
            if (!_updateView)
                return;
            
            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                if (_resetInterpolation)
                {
                    _resetInterpolation = false;
                    _interpolatedState.Teleport(_viewState.Value);
                }
                else _interpolatedState.Add(_viewState.Value);
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
