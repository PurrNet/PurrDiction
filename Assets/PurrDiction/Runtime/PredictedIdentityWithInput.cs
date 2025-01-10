using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<INPUT, STATE> : PredictedIdentity<STATE> 
        where STATE : struct 
        where INPUT : struct
    {
        private History<INPUT> _inputHistory;

        protected abstract INPUT GetInput();

        protected override void Setup(NetworkManager manager, PredictedWorld world)
        {
            base.Setup(manager, world);
            
            _inputHistory = new History<INPUT>(tickModule.tickRate * settings.secondsToKeepInHistory);
        }

        public override void Simulate()
        {
            Simulate(GetInput(), ref predictedState);
        }

        protected abstract void Simulate(INPUT input, ref STATE state);

        protected override void Simulate(ref STATE state)
        {
            Simulate(default, ref state);
        }

        public override void WriteState(ulong tick, BitPacker packer)
        {
            base.WriteState(tick, packer);
            
            if (_inputHistory.Read(tick, out var input))
                Packer<INPUT>.Write(packer, input);
        }

        public override void ReadState(ulong tick, BitPacker packer)
        {
            base.ReadState(tick, packer);
            
            INPUT input = default;
            Packer<INPUT>.Read(packer, ref input);
            _inputHistory.Write(tick, input);
        }
    }
}
