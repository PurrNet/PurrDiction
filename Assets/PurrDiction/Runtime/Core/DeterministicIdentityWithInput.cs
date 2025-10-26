using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class DeterministicIdentity<INPUT, STATE> : DeterministicIdentity<STATE>
        where STATE : struct, IPredictedData<STATE>
        where INPUT : struct, IPredictedData
    {
        [Header("Predicted Input")]
        [SerializeField] protected float _repeatInputFactor = 0.8f;
        [SerializeField] protected bool _extrapolateInput = true;

        public override bool hasInput => true;

        private History<INPUT> _inputHistory;

        public INPUT currentInput => _currentInput;
        private INPUT _currentInput;

        public override string ToString()
        {
            return $"State:\n{fullPredictedState.state}";
        }

        public override string GetExtraString()
        {
            return $"Input:\n{_lastInput}";
        }

        protected virtual void GetFinalInput(ref INPUT input) {}

        protected virtual void UpdateInput(ref INPUT input) { }

        private INPUT _lastInput;
        private INPUT _nextInput;

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            base.Setup(manager, world, id, owner);

            _inputHistory = new History<INPUT>(world.tickRate * 5);
        }

        internal override void SimulateTick(ulong tick, float delta)
        {
            var sdelta = sfloat.FromFloat(delta);
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }

            if (IsOwner())
            {
                if (!_inputHistory.TryGet(tick, out var input))
                    PreSimulate(GetDefaultInput(), ref fullPredictedState.state, sdelta);
                else PreSimulate(input, ref fullPredictedState.state, sdelta);
            }
            else
            {
                switch (_extrapolateInput)
                {
                    case true when _inputHistory.TryGetClosest(tick, out var extrainput, out var distanceInTicks):
                        if (distanceInTicks > 0)
                            ModifyExtrapolatedInput(ref extrainput);
                        uint maxInputs = (uint)Mathf.CeilToInt(_repeatInputFactor * 10 / (delta * 60));
                        if (distanceInTicks <= maxInputs)
                             PreSimulate(extrainput, ref fullPredictedState.state, sdelta);
                        else PreSimulate(GetDefaultInput(), ref fullPredictedState.state, sdelta);
                        break;
                    case false when _inputHistory.TryGet(tick, out var input):
                        PreSimulate(input, ref fullPredictedState.state, sdelta);
                        break;
                    default:
                        PreSimulate(GetDefaultInput(), ref fullPredictedState.state, sdelta);
                        break;
                }
            }
        }

        protected virtual void LateSimulate(INPUT input, ref STATE state, sfloat delta) {}

        internal override void LateSimulateTick(float delta)
        {
            LateSimulate(_currentInput, ref fullPredictedState.state, sfloat.FromFloat(delta));
        }

        /// <summary>
        /// Modify the extrapolated input before it is used to simulate the state.
        /// </summary>
        protected virtual void ModifyExtrapolatedInput(ref INPUT input) { }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate)
        {
            if (isLocal)
            {
                GetFinalInput(ref _nextInput);
                SanitizeInput(ref _nextInput);
                _lastInput = _nextInput;
                _inputHistory.Write(tick, _nextInput);
                _nextInput = GetDefaultInput();
            }
            else if (isServer)
            {
                if (_queuedInput == null)
                {
                    if (!extrapolate)
                        _lastInput = GetDefaultInput();
                    _inputHistory.Write(tick, _lastInput);
                    return;
                }

                var input = _queuedInput.Value;
                SanitizeInput(ref input);
                _lastInput = input;
                _inputHistory.Write(tick, input);
                _queuedInput = null;
            }
        }

        protected virtual void Update()
        {
            if(isController)
                UpdateInput(ref _nextInput);
        }

        protected virtual INPUT GetDefaultInput() => default;

        private void PreSimulate(INPUT input, ref STATE state, sfloat delta)
        {
            _currentInput = input;
            Simulate(input, ref state, delta);
        }

        protected abstract void Simulate(INPUT input, ref STATE state, sfloat delta);

        protected override void Simulate(ref STATE state, sfloat delta)
        {
            throw new System.NotImplementedException();
        }

        readonly struct DeltaKey : IStableHashable
        {
            private readonly PredictedComponentID id;

            public DeltaKey(PredictedComponentID id)
            {
                this.id = id;
            }

            public uint GetStableHash()
            {
                return (uint)id.GetHashCode() ^ Hasher<INPUT>.stableHash;
            }
        }

        DeltaKey key => new DeltaKey(id);

        public override void WriteFirstInput(ulong localTick, BitPacker packer)
        {
            int pos = packer.positionInBits;
            if (_inputHistory.TryGet(localTick, out var savedInput))
            {
                Packer<bool>.Write(packer, true);
                Packer<INPUT>.Write(packer, savedInput);
            }
            else
            {
                Packer<bool>.Write(packer, false);
            }
            TickBandwidthProfiler.OnWroteInput(myType, packer.positionInBits - pos, this);
        }

        public override void ReadFirstInput(ulong localTick, BitPacker packer)
        {
            var pos = packer.positionInBits;
            if (Packer<bool>.Read(packer))
            {
                var input = Packer<INPUT>.Read(packer);
                _inputHistory.Write(localTick, input);
            }
            else _inputHistory.Remove(localTick);

            TickBandwidthProfiler.OnReadInput(myType, packer.positionInBits - pos, this);
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable)
        {
            int pos = input.positionInBits;

            if (_inputHistory.TryGet(localTick, out var savedInput))
            {
                Packer<bool>.Write(input, true);

                if (reliable)
                    deltaModule.WriteReliable(input, receiver, key, savedInput);
                else deltaModule.Write(input, receiver, key, savedInput);
            }
            else
            {
                Packer<bool>.Write(input, false);
            }

            TickBandwidthProfiler.OnWroteInput(myType, input.positionInBits - pos, this);
        }

        internal override void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
            var pos = packer.positionInBits;

            if (Packer<bool>.Read(packer))
            {
                INPUT input = default;
                if (reliable)
                    deltaModule.ReadReliable(packer, key, ref input);
                else deltaModule.Read(packer, key, sender, ref input);
                _inputHistory.Write(tick, input);
            }
            else _inputHistory.Remove(tick);

            TickBandwidthProfiler.OnReadInput(myType, packer.positionInBits - pos, this);
        }

        private INPUT? _queuedInput;

        /// <summary>
        /// Sanitize the input before using it.
        /// Use this to clamp values or prevent invalid input.
        /// </summary>
        /// <param name="input"></param>
        protected virtual void SanitizeInput(ref INPUT input) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable)
        {
            int pos = packer.positionInBits;
            if (Packer<bool>.Read(packer))
            {
                INPUT input = default;

                if (reliable)
                    deltaModule.ReadReliable(packer, key, ref input);
                else deltaModule.Read(packer, key, sender, ref input);

                var sanitizedInput = input;
                SanitizeInput(ref sanitizedInput);
                _queuedInput = sanitizedInput;
            }
            TickBandwidthProfiler.OnReadInput(myType, packer.positionInBits - pos, this);
        }
    }
}
