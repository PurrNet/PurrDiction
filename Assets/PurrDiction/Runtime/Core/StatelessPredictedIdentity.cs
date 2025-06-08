using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class StatelessPredictedIdentity : PredictedIdentity
    {
        protected virtual void Simulate(float delta) {}

        internal override void SimulateTick(ulong tick, float delta) => Simulate(delta);

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick)
        {
        }

        internal override void SimulateRemote(ulong tick, float delta)
        {
        }

        internal override void SaveStateInHistory(ulong tick)
        {
        }

        internal override void Rollback(ulong tick)
        {
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
        }

        public override void ResetInterpolation()
        {
        }

        internal override void UpdateView(float deltaTime)
        {
        }

        internal override void GetLatestUnityState()
        {
        }

        internal override void WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule, ref PackedUInt cache)
        {
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule delta, ref PackedUInt cache)
        {
        }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule delta, ref PackedUInt cache)
        {
        }

        internal override void ReadInput(ulong tick, BitPacker packer, DeltaModule delta, ref PackedUInt cache)
        {
        }

        internal override void QueueInput(PlayerID sender, BitPacker packer, DeltaModule delta, ref PackedUInt cache)
        {
        }
    }
}
