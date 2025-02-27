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
        [Header("Predicted Input")]
        [SerializeField] protected float _repeatInputFactor = 0.8f;
        [SerializeField] protected bool _extrapolateInput = true;

        private History<INPUT> _inputHistory;

        protected abstract INPUT GetInput();

        private INPUT _lastInput;

        public PredictedHierarchy hierarchy { get; private set; }

        internal override void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            if (!isFreshSpawn)
                return;

            base.Setup(manager, world, id);

            hierarchy = world.hierarchy;
            _inputHistory = new History<INPUT>(world.tickRate * 5);
        }

        internal override void SimulateTick(ulong tick, FP delta)
        {
            if (IsOwner())
            {
                if (!_inputHistory.TryGet(tick, out var input))
                     Simulate(default, ref fullPredictedState.state, delta);
                else Simulate(input, ref fullPredictedState.state, delta);
            }
            else
            {
                switch (_extrapolateInput)
                {
                    case true when _inputHistory.TryGetClosest(tick, out var extrainput, out var distanceInTicks):
                        ModifyExtrapolatedInput(ref extrainput);
                        uint maxInputs = (uint)Mathf.CeilToInt(_repeatInputFactor * 10 / ((float)delta * 60));
                        if (distanceInTicks <= maxInputs)
                             Simulate(extrainput, ref fullPredictedState.state, delta);
                        else Simulate(null, ref fullPredictedState.state, delta);
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

        /// <summary>
        /// Modify the extrapolated input before it is used to simulate the state.
        /// </summary>
        protected virtual void ModifyExtrapolatedInput(ref INPUT input) { }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick)
        {
            if (isLocal)
            {
                var input = GetInput();
                SanitizeInput(ref input);
                _lastInput = input;
                _inputHistory.Write(tick, input);
            }
            else if (isServer)
            {
                INPUT input;

                if (_queuedInputs.Count == 0)
                {
                    if (!_inputHistory.TryGetClosest(tick, out input))
                        input = _lastInput;
                }
                else input = _queuedInputs.Dequeue();

                SanitizeInput(ref input);
                _lastInput = input;
                _inputHistory.Write(tick, input);
            }
        }

        internal override void SimulateRemote(ulong tick, FP delta)
        {
            if (_inputHistory.TryGet(tick, out var input))
                Simulate(input, ref fullPredictedState.state, delta);
            else Simulate(null, ref fullPredictedState.state, delta);
        }

        protected abstract void Simulate(INPUT? input, ref STATE state, FP delta);

        protected override void Simulate(ref STATE state, FP delta)
        {
            Simulate(_lastInput, ref state, delta);
        }

        public override void WriteInput(ulong localTick, BitPacker input)
        {
            if (_inputHistory.TryGet(localTick, out var savedInput))
                 Packer<INPUT?>.Write(input, savedInput);
            else Packer<INPUT?>.Write(input, null);
        }

        public override void ReadInput(ulong tick, BitPacker packer)
        {
            INPUT? input = default;
            Packer<INPUT?>.Read(packer, ref input);

            if (input.HasValue)
                 _inputHistory.Write(tick, input.Value);
            else _inputHistory.Remove(tick);
        }

        readonly Queue<INPUT> _queuedInputs = new();

        /// <summary>
        /// Sanitize the input before using it.
        /// Use this to clamp values or prevent invalid input.
        /// </summary>
        /// <param name="input"></param>
        protected virtual void SanitizeInput(ref INPUT input) { }

        public override void QueueInput(BitPacker packer)
        {
            INPUT? input = default;
            Packer<INPUT?>.Read(packer, ref input);

            if (input.HasValue)
            {
                var sanitizedInput = input.Value;
                SanitizeInput(ref sanitizedInput);
                _queuedInputs.Enqueue(sanitizedInput);
            }
        }

        public override void ClearInput()
        {
            _queuedInputs.Clear();
        }
    }
}
