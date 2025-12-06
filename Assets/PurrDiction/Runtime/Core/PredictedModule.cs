using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule
    {
        public PredictedIdentity identity { get; private set; }
        public PredictionManager manager { get; private set; }

        public void Setup(PredictedIdentity identity, PredictionManager manager)
        {
            this.identity = identity;
            this.manager = manager;
            OnSetup();
        }

        protected virtual void OnSetup() { }

        public abstract void Simulate(ulong tick, float delta);
        public abstract void LateSimulate(float delta);
        public abstract void Rollback(ulong tick);
        public abstract void SaveState(ulong tick);
        public abstract void WriteState(BitPacker packer, DeltaModule deltaModule);
        public abstract void ReadState(BitPacker packer, DeltaModule deltaModule);
        public virtual void UpdateInterpolation(float delta, bool accumulateError) { }
        public virtual void ResetInterpolation() { }
    }
}