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

        internal virtual void SimulateRemote(ulong tick, float delta)
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

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            return false;
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule delta, bool reliable)
        {
        }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule delta)
        {
        }

        internal override void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule delta, bool reliable)
        {
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule delta, bool reliable)
        {
        }
    }
}
