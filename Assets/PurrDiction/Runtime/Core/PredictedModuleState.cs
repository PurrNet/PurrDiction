using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        internal FULL_STATE<TState> fullPredictedState;
        
        public ref TState currentState => ref fullPredictedState.state;

        private History<FULL_STATE<TState>> _history = new History<FULL_STATE<TState>>();
        public PredictedModule(PredictedIdentity identity) : base(identity) { }

        protected ModuleDeltaKey<PredictedIdentityState> predictionKey => new ModuleDeltaKey<PredictedIdentityState>(identity.id, moduleIndex);
        protected ModuleDeltaKey<TState> stateKey => new ModuleDeltaKey<TState>(identity.id, moduleIndex);

        protected override void Simulate(ulong tick, float delta)
        {
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }
            Simulate(ref fullPredictedState.state, delta);
        }

        protected virtual void SimulationStart() { }
        
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