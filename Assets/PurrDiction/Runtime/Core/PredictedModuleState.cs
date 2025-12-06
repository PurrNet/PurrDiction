using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        protected History<TState> history { get; private set; }
        public TState state;
        
        protected PredictedModule(PredictedIdentity identity, TState state) : base(identity)
        {
            this.state = state;
            history = new History<TState>();
        }

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
            
            bool changed = deltaModule.WriteReliable(packer, receiver, GetDeltaKey(), state);
            packer.WriteAt(flagPos, changed);

            if (!changed)
            {
                packer.SetBitPosition(flagPos + 1);
            }

            //Not sure if we need this?
            //TickBandwidthProfiler.OnWroteState(this.GetType().Name, packer.positionInBits - pos, identity);

            return changed;
        }

        public override void ReadState(BitPacker packer, DeltaModule deltaModule)
        {
            bool changed = packer.ReadBool();

            if (changed)
            {
                deltaModule.ReadReliable(packer, GetDeltaKey(), ref state);
            }
        }
    }
}
