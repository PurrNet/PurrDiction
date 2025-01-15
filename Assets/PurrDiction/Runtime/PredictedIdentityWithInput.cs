using System.Collections.Generic;
using FixMath.NET;
using PurrNet.Logging;
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
        
        internal override void EvaluateLocalInput(ulong localTick)
        {
            _lastInput = GetInput();
            _inputHistory.Write(localTick, _lastInput);
        }
        
        internal override void WriteLocalInput(BitPacker packet)
        {
            Packer<INPUT>.Write(packet, _lastInput);
        }
        
        internal override void SimulateTick(ulong tick, Fix64 delta)
        {
            if (IsOwner())
            {
                if (!_inputHistory.TryGet(tick, out var input))
                    Simulate(GetInput(), delta);
                else Simulate(input, delta);
            }
            else
            {
                if (!_inputHistory.TryGetClosest(tick, out var input))
                     Simulate(null, delta);
                else Simulate(input, delta);
            }
        }

        internal override void SimulateLocal(Fix64 delta)
        {
            Simulate(_lastInput, delta);
        }
        
        internal override void SimulateRemote(Fix64 delta)
        {
            if (_queuedInputs.Count == 0)
            {
                Simulate(_lastInput, delta);
                return;
            }
            
            var input = _queuedInputs[0];
            _queuedInputs.RemoveAt(0);
            _lastInput = input.input;
            
            Simulate(_lastInput, delta);
        }

        protected abstract void Simulate(INPUT? input, Fix64 delta);
        
        protected override void Simulate(Fix64 delta)
        {
            Simulate(_lastInput, delta);
        }

        public override void WriteInput(ulong localTick, BitPacker input)
        {
            if (_inputHistory.TryGetClosest(localTick, out var savedInput))
                 Packer<INPUT>.Write(input, savedInput);
            else Packer<INPUT>.Write(input, default);
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
