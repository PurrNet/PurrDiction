using System;
using FixMath.NET;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<INPUT, STATE> : PredictedIdentity<STATE> 
        where STATE : struct, IDisposable
        where INPUT : struct, IDisposable
    {
        private History<INPUT> _inputHistory;

        protected abstract INPUT GetInput();

        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            base.Setup(manager, world);

            _inputHistory = new History<INPUT>(world.tickRate * settings.secondsToKeepInHistory);
        }

        internal override void Simulate(ulong tick, Fix64 delta)
        {
            bool isFuture = tick > _inputHistory.MostRecentTick;

            INPUT input;
            
            if (isFuture)
                input = GetInput();
            else if (_inputHistory.TryGetClosest(tick, out var savedInput))
                input = savedInput;
            else input = GetInput();
            
            _inputHistory.Write(tick, input);

            Simulate(input, delta);
            PostSimulate(tick);
        }

        protected abstract void Simulate(INPUT input, Fix64 delta);

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
