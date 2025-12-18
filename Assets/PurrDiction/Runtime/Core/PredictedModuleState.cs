using System;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        internal FULL_STATE<TState> fullPredictedState;
        
        /// <summary>
        /// The current simulation relevant state
        /// </summary>
        public ref TState currentState => ref fullPredictedState.state;

        private History<FULL_STATE<TState>> _history = new History<FULL_STATE<TState>>();
        
        private InterpolatedWithDispose<FULL_STATE<TState>> _interpolatedState;
        private FULL_STATE<TState>? _viewState;

        public TState viewState;
        
        /// <summary>
        /// The last fully verified state received from the server (or authoritative state if local).
        /// Returns null if no history exists yet.
        /// </summary>
        public TState? verifiedState => _history.Count > 0 ? _history[^1].state : null;

        public PredictedModule(PredictedIdentity identity) : base(identity) { }

        protected ModuleDeltaKey<PredictedIdentityState> predictionKey => new ModuleDeltaKey<PredictedIdentityState>(identity.id, moduleIndex);
        protected ModuleDeltaKey<TState> stateKey => new ModuleDeltaKey<TState>(identity.id, moduleIndex);

        internal override void OnCoreInitialize()
        {
            var tickRate = manager.tickRate;
            var bufferSize = (int)Math.Max(tickRate / 10f, 2);
            
            _interpolatedState = new InterpolatedWithDispose<FULL_STATE<TState>>(
                FULLInterpolate, 
                1f / tickRate, 
                fullPredictedState.DeepCopy(), 
                bufferSize
            );
        }

        protected sealed override void UpdateView(float delta)
        {
            if (_interpolatedState == null) return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            var result = _interpolatedState.Advance(delta);
            viewState = result.state;
            
            UpdateView(viewState, verifiedState);
        }
        
        /// <summary>
        /// Override this to apply visual updates based on the interpolated state.
        /// (e.g., transforming a GameObject based on viewState.position).
        /// </summary>
        /// <param name="viewState">The smooth, interpolated state for the current frame.</param>
        /// <param name="verifiedState">The latest authoritative state, useful for comparisons or error correction.</param>
        protected virtual void UpdateView(TState viewState, TState? verifiedState) { }
        
        /// <summary>
        /// Override to handle interpolation between states manually
        /// </summary>
        /// <param name="from">State to interpolate from</param>
        /// <param name="to">State to interpolate to</param>
        /// <param name="t">Step</param>
        /// <returns>The interpolated state at the given step</returns>
        protected virtual TState Interpolate(TState from, TState to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        private FULL_STATE<TState> FULLInterpolate(FULL_STATE<TState> from, FULL_STATE<TState> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE<TState>
            {
                state = state,
                prediction = from.prediction
            };
        }

        protected override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }
        
        protected override void UpdateInterpolation(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        /// <summary>
        /// Allows modification of the state used for rollback interpolation before it is committed.
        /// Useful for accumulating prediction error into the visual state to smooth out corrections.
        /// </summary>
        protected virtual void ModifyRollbackViewState(ref TState state, float delta, bool accumulateError) { }

        protected override void Simulate(ulong tick, float delta)
        {
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }
            Simulate(ref fullPredictedState.state, delta);
        }

        /// <summary>
        /// Called exactly once before the very first simulation tick executes.
        /// </summary>
        protected virtual void SimulationStart() { }
        
        /// <summary>
        /// Executes the simulation logic for this module.
        /// Modify the <paramref name="state"/> directly to advance the simulation. Or use the `currentState`
        /// </summary>
        /// <param name="state">Reference to the current simulation state.</param>
        /// <param name="delta">Time in seconds since the last tick.</param>
        protected virtual void Simulate(ref TState state, float delta) { }

        protected override void Rollback(ulong tick)
        {
            if (_history.Read(tick, out var result))
            {
                fullPredictedState = result.DeepCopy();
            }
        }

        protected override void SaveState(ulong tick)
        {
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            int flagPos = packer.AdvanceBits(1);

            bool changed = deltaModule.WriteReliable(packer, receiver, predictionKey, fullPredictedState.prediction);
            changed |= deltaModule.WriteReliable(packer, receiver, stateKey, fullPredictedState.state);

            packer.WriteAt(flagPos, changed);
            
            if (!changed)
                packer.SetBitPosition(flagPos + 1);

            return changed;
        }

        protected override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            bool changed = Packer<bool>.Read(packer);

            if (changed)
            {
                deltaModule.ReadReliable(packer, predictionKey, ref fullPredictedState.prediction);
                deltaModule.ReadReliable(packer, stateKey, ref fullPredictedState.state);
                _history.Write(tick, fullPredictedState.DeepCopy());
            }
            else
            {
                packer.SetBitPosition(pos);
                deltaModule.ReadReliable(packer, predictionKey, ref fullPredictedState.prediction);
                
                packer.SetBitPosition(pos);
                deltaModule.ReadReliable(packer, stateKey, ref fullPredictedState.state);
                
                _history.Write(tick, fullPredictedState.DeepCopy());
            }
        }

        protected override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _history.TryGet(tick, out var historyState))
                savedState = historyState;

            Packer<PredictedIdentityState>.Write(packer, savedState.prediction);
            Packer<TState>.Write(packer, savedState.state);
        }

        protected override void ReadFirstState(ulong tick, BitPacker packer)
        {
            Packer<PredictedIdentityState>.Read(packer, ref fullPredictedState.prediction);
            Packer<TState>.Read(packer, ref fullPredictedState.state);
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override void ClearFuture(ulong tick)
        {
            _history.ClearFuture(tick);
        }
    }
}