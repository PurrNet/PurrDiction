using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    public readonly struct ModuleDeltaKey<T> : IStableHashable
    {
        private readonly PredictedComponentID id;
        private readonly int moduleIndex;

        public ModuleDeltaKey(PredictedComponentID id, int moduleIndex)
        {
            this.id = id;
            this.moduleIndex = moduleIndex;
        }

        public uint GetStableHash()
        {
            return Hasher<T>.stableHash ^ id.componentId.value ^ id.objectId.instanceId.value ^ (uint)moduleIndex;
        }
    }

    public abstract class PredictedModule
    {
        public PredictedIdentity identity { get; private set; }
        public PredictionManager manager { get; private set; }
        
        public int moduleIndex { get; internal set; }

        public PredictedModule(PredictedIdentity identity)
        {
            this.identity = identity;
            this.manager = identity.predictionManager;
        
            identity.RegisterModule(this);
        
            OnInitialize();
        }

        protected virtual void OnInitialize() { }
        
        public virtual void Setup(PredictedIdentity parent, PredictionManager world) { }

        public virtual void Simulate(ulong tick, float delta) { }
        public virtual void LateSimulate(float delta) { }
        public abstract void Rollback(ulong tick);
        public abstract void SaveState(ulong tick);
        public abstract bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);
        public abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule);
        
        public abstract void WriteFirstState(ulong tick, BitPacker packer);
        public abstract void ReadFirstState(ulong tick, BitPacker packer);
        public abstract void ClearFuture(ulong tick);

        public virtual void UpdateInterpolation(float delta, bool accumulateError) { }
        public virtual void ResetInterpolation() { }
    }
}