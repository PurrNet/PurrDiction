using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public sealed class StaticPredictedIdentity : PredictedIdentity
    {
        internal override void SimulateTick(ulong tick, float delta)
        {
        }

        internal override void LateSimulateTick(float delta)
        {
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate)
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

        internal override void GetLatestUnityState()
        {
        }

        internal override bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            return false;
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable)
        {
        }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
        }

        internal override void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable)
        {
        }
    }
}
