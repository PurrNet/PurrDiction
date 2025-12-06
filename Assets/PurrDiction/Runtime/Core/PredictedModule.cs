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

        internal void SetupInternal(PredictedIdentity parent, PredictionManager world) => Setup(parent, world);
        
        
        protected virtual void Setup(PredictedIdentity parent, PredictionManager world) { }

        internal void SimulateInternal(ulong tick, float delta) => Simulate(tick, delta);
        
        protected virtual void Simulate(ulong tick, float delta) { }
        internal void LateSimulateInternal(float delta) => LateSimulate(delta);
        protected virtual void LateSimulate(float delta) { }
        internal void RollbackInternal(ulong tick) => Rollback(tick);
        protected abstract void Rollback(ulong tick);
        internal void SaveStateInternal(ulong tick) => SaveState(tick);
        protected abstract void SaveState(ulong tick);
        internal bool WriteStateInternal(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)=> WriteState(receiver, packer, deltaModule);
        protected abstract bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);
        internal void ReadStateInternal(ulong tick, BitPacker packer, DeltaModule deltaModule) => ReadState(tick, packer, deltaModule);
        protected abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule);

        internal void WriteFirstStateInternal(ulong tick, BitPacker packer) => WriteFirstState(tick, packer);
        protected abstract void WriteFirstState(ulong tick, BitPacker packer);
        internal void ReadFirstStateInternal(ulong tick, BitPacker packer) => ReadFirstState(tick, packer);
        protected abstract void ReadFirstState(ulong tick, BitPacker packer);
        internal void ClearFutureInternal(ulong tick) => ClearFuture(tick);
        protected abstract void ClearFuture(ulong tick);

        internal void UpdateInterpolationInternal(float delta, bool accumulateError) => UpdateInterpolation(delta, accumulateError);
        protected virtual void UpdateInterpolation(float delta, bool accumulateError) { }
        internal void ResetInterpolationInternal() => ResetInterpolation();
        protected virtual void ResetInterpolation() { }
    }
}