using System;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public class PredictionSettings
    {
        [Tooltip("Maximum number of inputs to buffer before dropping old ones.")]
        public int maxInputBufferCount = 4;
        
        [Tooltip("The number of seconds to keep in the history for rollback purposes and redundancy.\n" +
                 "Naturally, this means more memory usage.")]
        public int secondsToKeepInHistory = 5;
    }
    
    public abstract class PredictedIdentity<STATE> : MonoBehaviour 
        where STATE : struct
    {
        [SerializeField] PredictionSettings _predictionSettings;
        
        protected PredictionSettings settings => _predictionSettings;
        
        public PredictedWorld predictedWorld { get; private set; }

        private Interpolated<STATE> _interpolatedState;
        
        private History<STATE> _stateHistory;
        
        protected STATE predictedState;

        protected STATE? verifiedState;
        
        private STATE _initialState;
        
        protected TickManager tickModule { get; private set; }
        
        protected float tickDelta { get; private set; }

        /*protected virtual void Start()
        {
            if (PredictedWorld.TryGetInstance(gameObject.scene.handle, out var world))
            {
                world.Register(this);
            }
        }*/

        protected virtual void Setup(NetworkManager manager, PredictedWorld world)
        {
            predictedWorld = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            tickDelta = tickModule.tickDelta;
            
            predictedState = InitializeState();
            
            _interpolatedState = new Interpolated<STATE>(Interpolate, tickDelta, predictedState, settings.maxInputBufferCount);
            _stateHistory = new History<STATE>(tickModule.tickRate * settings.secondsToKeepInHistory);
            _initialState = InitializeState();
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected abstract STATE InitializeState();

        public virtual void Simulate()
        {
            Simulate(ref predictedState);
        }
        
        protected abstract void Simulate(ref STATE state);

        public virtual void Rollback(ulong tick)
        {
            if (!_stateHistory.TryGetClosest(tick, out var state))
                state = _initialState;
            
            predictedState = state;
            _stateHistory.ClearFuture(tick);
        }

        [UsedImplicitly]
        public virtual void WriteState(ulong tick, BitPacker packer)
        {
            if (_stateHistory.Read(tick, out var state))
                Packer<STATE>.Write(packer, state);
        }

        [UsedImplicitly]
        public virtual void ReadState(ulong tick, BitPacker packer)
        {
            STATE state = default;
            Packer<STATE>.Read(packer, ref state);
            _stateHistory.Write(tick, state);
        }
        
        public virtual void UpdateView(STATE predicted, STATE? verified) {}

        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            return to;
        }
    }
}
