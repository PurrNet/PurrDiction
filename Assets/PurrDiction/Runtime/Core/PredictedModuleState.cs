using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        protected History<TState> history { get; private set; }
        public TState state;

        protected override void OnSetup()
        {
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

        public override void WriteState(BitPacker packer, DeltaModule deltaModule)
        {
            Packer<TState>.Write(packer, state);
        }

        public override void ReadState(BitPacker packer, DeltaModule deltaModule)
        {
            Packer<TState>.Read(packer, ref state);
        }
    }
}
