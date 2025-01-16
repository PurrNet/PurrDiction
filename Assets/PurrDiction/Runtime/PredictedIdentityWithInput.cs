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
        
        internal override void EvaluateAndRegisterLocalInput(ulong localTick)
        {
            _lastInput = GetInput();
            _inputHistory.Write(localTick, _lastInput);
        }
        
        internal override void SimulateTick(ulong tick, Fix64 delta)
        {
            if (IsOwner())
            {
                Simulate(!_inputHistory.TryGet(tick, out var input) ? GetInput() : input, delta);
            }
            else
            {
                Simulate(_inputHistory.TryGetClosest(tick, out var input) ? input : default, delta);
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
            
            _lastInput = _queuedInputs.Dequeue();
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
        
        readonly Queue<INPUT> _queuedInputs = new();

        public override void QueueInput(BitPacker packer)
        {
            INPUT input = default;
            Packer<INPUT>.Read(packer, ref input);
            _queuedInputs.Enqueue(input);

            while (_queuedInputs.Count > predictionManager.maxInputQueue)
                _queuedInputs.Dequeue();
        }
    }
}
