using System;
using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity : MonoBehaviour
    {
        public abstract void Setup(NetworkManager manager, PredictionManager world);

        protected void Awake()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.RegisterInstance(this);
        }

        internal abstract void PreSimulate(ulong tick);
        
        internal abstract void Simulate(ulong tick, Fix64 delta);

        internal abstract void Rollback(ulong tick);
        
        internal abstract void UpdateView(float deltaTime);
        
        public abstract void WriteState(ulong tick, BitPacker packer, bool asServer);

        public abstract void ReadState(ulong tick, BitPacker packer, bool asServer);
    }
    
    public abstract class PredictedIdentity<STATE> : PredictedIdentity 
        where STATE : struct, IDisposable
    {
        [SerializeField] PredictionSettings _predictionSettings;
        
        protected PredictionSettings settings => _predictionSettings;
        
        public PredictionManager predictionManager { get; private set; }

        private Interpolated<STATE> _interpolatedState;
        
        private History<STATE> _stateHistory;
        
        protected STATE predictedState;

        protected STATE? verifiedState;
        
        protected TickManager tickModule { get; private set; }
        
        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            predictionManager = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            predictedState = GetCurrentState();
            
            _predictionSettings ??= new PredictionSettings();
            
            _interpolatedState = new Interpolated<STATE>(Interpolate, (float)world.tickDelta, predictedState, settings.maxInputBufferCount);
            _stateHistory = new History<STATE>(world.tickRate * settings.secondsToKeepInHistory);
            _stateHistory.Write(0, predictedState);
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected abstract STATE GetCurrentState();

        internal override void PreSimulate(ulong tick) { }

        internal override void Simulate(ulong tick, Fix64 delta)
        {
            Simulate(delta);
            PostSimulate(tick);
        }

        protected void PostSimulate(ulong tick)
        {
            predictedState = GetCurrentState();
            _stateHistory.Write(tick, predictedState);
            _interpolatedState.Add(predictedState);
        }

        protected abstract void Simulate(Fix64 delta);

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.TryGetClosest(tick, out var state))
                state = predictedState;
            
            verifiedState = state;
            _stateHistory.ClearFuture(tick);
            Rollback(state);
        }
        
        protected abstract void Rollback(STATE state);

        [UsedImplicitly]
        public override void WriteState(ulong tick, BitPacker packer, bool asServer)
        {
            if (asServer && _stateHistory.Read(tick, out var state))
                Packer<STATE>.Write(packer, state);
        }

        [UsedImplicitly]
        public override void ReadState(ulong tick, BitPacker packer, bool asServer)
        {
            if (asServer)
                return;
            
            STATE state = default;
            Packer<STATE>.Read(packer, ref state);
            _stateHistory.Write(tick, state);
        }

        internal override void UpdateView(float deltaTime)
        {
            if (_interpolatedState == null)
                return;
            
            var interpolatedState = _interpolatedState.Advance(deltaTime);
            UpdateView(interpolatedState, null);
        }

        protected virtual void UpdateView(STATE predicted, STATE? verified) {}

        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            return to;
        }
    }
}
