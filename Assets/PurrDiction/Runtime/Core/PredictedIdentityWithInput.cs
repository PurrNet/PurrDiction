using System.Collections.Generic;
using FixMath.NET;
using PurrNet.Logging;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<INPUT, STATE> : PredictedIdentity<STATE>
        where STATE : struct, IPredictedData<STATE>
        where INPUT : struct, IPredictedData
    {
        [SerializeField] private bool _extrapolateInput = true;
        
        private History<INPUT> _inputHistory;

        protected abstract INPUT GetInput();
        
        private INPUT _lastInput;
        
        public PredictedHierarchy hierarchy { get; private set; }

        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            if (!isFreshSpawn)
                return;
            
            base.Setup(manager, world);

            hierarchy = world.hierarchy;
            _inputHistory = new History<INPUT>(world.tickRate * 5);
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
                if (!_inputHistory.TryGet(tick, out var input))
                {
                    Simulate(GetInput(), ref fullPredictedState.state, delta);
                    PurrLogger.LogError("No local input found for tick" + tick + " using current input but this is likely a bug");
                }
                else Simulate(input, ref fullPredictedState.state, delta);
            }
            else
            {
                switch (_extrapolateInput)
                {
                    case true when _inputHistory.TryGetClosest(tick, out var extrainput):
                        Simulate(extrainput, ref fullPredictedState.state, delta);
                        break;
                    case false when _inputHistory.TryGet(tick, out var input):
                        Simulate(input, ref fullPredictedState.state, delta);
                        break;
                    default:
                        Simulate(null, ref fullPredictedState.state, delta);
                        break;
                }
            }
        }

        internal override void SimulateLocal(Fix64 delta)
        {
            Simulate(_lastInput, ref fullPredictedState.state, delta);
        }
        
        internal override void SimulateRemote(ulong tick, Fix64 delta)
        {
            if (_queuedInputs.Count == 0)
            {
                if (_inputHistory.TryGetClosest(tick, out var input))
                     Simulate(input, ref fullPredictedState.state, delta);
                else Simulate(_lastInput, ref fullPredictedState.state, delta);
                
                return;
            }

            var dequeuedInput = _queuedInputs.Dequeue();
            _inputHistory.Write(tick, dequeuedInput);
            Simulate(dequeuedInput, ref fullPredictedState.state, delta);
        }

        protected abstract void Simulate(INPUT? input, ref STATE state, Fix64 delta);
        
        protected override void Simulate(Fix64 delta, ref STATE state)
        {
            Simulate(_lastInput, ref state, delta);
        }

        public override void WriteInput(ulong localTick, BitPacker input)
        {
            if (_inputHistory.TryGetClosest(localTick, out var savedInput))
                 Packer<INPUT>.Write(input, savedInput);
            else Packer<INPUT>.Write(input, _lastInput);
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
        }

        public override void ClearInput()
        {
            _queuedInputs.Clear();
        }
    }
}
