using System;
using System.Collections.Generic;
using FixMath.NET;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<INPUT, STATE> : PredictedIdentity<STATE> 
        where STATE : struct, IOptionalDispose
        where INPUT : struct, IOptionalDispose
    {
        private History<INPUT> _inputHistory;

        protected abstract INPUT GetInput();
        
        private INPUT _lastInput;

        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            base.Setup(manager, world);

            _inputHistory = new History<INPUT>(world.tickRate * settings.secondsToKeepInHistory);
        }
        
        internal override void EvaluateLocalInput()
        {
            _lastInput = GetInput();
        }
        
        internal override void WriteLocalInput(BitPacker packet)
        {
            Packer<INPUT>.Write(packet, _lastInput);
        }

        internal override void SimulateLocal(ulong tick, Fix64 delta)
        {
            Simulate(_lastInput, delta);
        }
        
        internal override void SimulateRemote(ulong tick, Fix64 delta)
        {
            if (_queuedInputs.Count == 0)
            {
                Simulate(null, delta);
                return;
            }
        }

        protected abstract void Simulate(INPUT? input, Fix64 delta);

        protected override void Simulate(Fix64 delta)
        {
            Simulate(default, delta);
        }

        public override void WriteInput(ulong localTick, BitPacker input)
        {
            if (_inputHistory.Read(localTick, out var savedInput))
                Packer<INPUT>.Write(input, savedInput);
        }

        public override void ReadInput(ulong tick, BitPacker packer)
        {
            INPUT input = default;
            Packer<INPUT>.Read(packer, ref input);
            _inputHistory.Write(tick, input);
        }
        
        struct QueuedInput
        {
            public ulong tick;
            public INPUT input;
        }
        
        readonly List<QueuedInput> _queuedInputs = new();

        public override void QueueInput(ulong tick, BitPacker packer)
        {
            INPUT input = default;
            Packer<INPUT>.Read(packer, ref input);
            
            int insertPos = 0;
            
            for (int i = 0; i < _queuedInputs.Count; i++)
            {
                if (_queuedInputs[i].tick < tick)
                    insertPos = i + 1;
            }
            
            _queuedInputs.Insert(insertPos, new QueuedInput
            {
                tick = tick,
                input = input
            });
            
            if (_queuedInputs.Count > settings.maxInputBufferCount)
                _queuedInputs.RemoveAt(0);
        }
    }
}
