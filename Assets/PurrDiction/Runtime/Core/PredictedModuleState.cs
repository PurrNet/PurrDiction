using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        protected History<TState> history { get; private set; } = new History<TState>();
        public TState state;

        public PredictedModule(PredictedIdentity identity) : base(identity) { }

        protected ModuleDeltaKey<TState> deltaKey => new ModuleDeltaKey<TState>(identity.id, moduleIndex);

        public override void Rollback(ulong tick)
        {
            if (history.Read(tick, out var result))
            {
                state = result;
            }
        }

        public override void SaveState(ulong tick)
        {
            history.Write(tick, state);
        }

        public override bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            int flagPos = packer.AdvanceBits(1);

            bool changed = deltaModule.WriteReliable(packer, receiver, deltaKey, state);

            packer.WriteAt(flagPos, changed);
            
            if (!changed)
                packer.SetBitPosition(flagPos + 1);

            return changed;
        }

        public override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            bool changed = Packer<bool>.Read(packer);

            if (changed)
            {
                deltaModule.ReadReliable(packer, deltaKey, ref state);
                history.Write(tick, state);
            }
            else
            {
                packer.SetBitPosition(pos);
                deltaModule.ReadReliable(packer, deltaKey, ref state);
                history.Write(tick, state);
            }
        }
    }
}