using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule
    {
        public PredictedIdentity identity { get; private set; }
        public PredictionManager manager { get; private set; }
        
        public int moduleIndex { get; internal set; }
        
        public PredictedModule(PredictedIdentity identity)
        {
            this.identity = identity;
            this.manager = identity.predictionManager;
        
            // Internal registration call
            identity.RegisterModule(this);
        
            OnInitialize();
        }

        protected virtual void OnInitialize() { }
        
        protected int GetDeltaKey()
        {
            return System.HashCode.Combine(identity.id, moduleIndex);
        }

        public abstract void Simulate(ulong tick, float delta);
        public abstract void LateSimulate(float delta);
        public abstract void Rollback(ulong tick);
        public abstract void SaveState(ulong tick);
        public abstract bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);
        public abstract void ReadState(BitPacker packer, DeltaModule deltaModule);
        public virtual void UpdateInterpolation(float delta, bool accumulateError) { }
        public virtual void ResetInterpolation() { }
    }
}