using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
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

        public override string ToString()
        {
            return $"State:\n{fullPredictedState.state}";
        }

        public override string GetExtraString()
        {
            return $"Input:\n{_lastInput}";
        }

        protected abstract INPUT GetInput();

        private INPUT _lastInput;

        public PredictedHierarchy hierarchy { get; private set; }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedID id, PlayerID? owner)
        {
            base.Setup(manager, world, id, owner);

            hierarchy = world.hierarchy;
            _inputHistory = new History<INPUT>(world.tickRate * 5);
        }

        internal override void SimulateTick(ulong tick, float delta)
        {
            if (IsOwner())
            {
                if (!_inputHistory.TryGet(tick, out var input))
                     Simulate(GetDefaultInput(), ref fullPredictedState.state, delta);
                else Simulate(input, ref fullPredictedState.state, delta);
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
                             Simulate(extrainput, ref fullPredictedState.state, delta);
                        else Simulate(GetDefaultInput(), ref fullPredictedState.state, delta);
                        break;
                    case false when _inputHistory.TryGet(tick, out var input):
                        Simulate(input, ref fullPredictedState.state, delta);
                        break;
                    default:
                        Simulate(GetDefaultInput(), ref fullPredictedState.state, delta);
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
                if (_queuedInput.Count == 0)
                {
                    _lastInput = GetDefaultInput();
                    _inputHistory.Write(tick, _lastInput);
                    return;
                }

                var input = _queuedInput.Dequeue();
                SanitizeInput(ref input);
                _lastInput = input;
                _inputHistory.Write(tick, input);
            }
        }

        internal override void SimulateRemote(ulong tick, float delta)
        {
            if (_inputHistory.TryGet(tick, out var input))
                Simulate(input, ref fullPredictedState.state, delta);
            else Simulate(GetDefaultInput(), ref fullPredictedState.state, delta);
        }

        protected virtual INPUT GetDefaultInput() => default;

        protected abstract void Simulate(INPUT input, ref STATE state, float delta);

        protected override void Simulate(ref STATE state, float delta)
        {
            Simulate(_lastInput, ref state, delta);
        }

        readonly struct DeltaKey : IStableHashable
        {
            private readonly PredictedID id;

            public DeltaKey(PredictedID id)
            {
                this.id = id;
            }

            public uint GetStableHash()
            {
                return (uint)id.GetHashCode() ^ Hasher<INPUT>.stableHash;
            }
        }

        DeltaKey key => new DeltaKey(id);

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule)
        {
            if (_inputHistory.TryGet(localTick, out var savedInput))
            {
                Packer<bool>.Write(input, true);

                using var tmp = BitPackerPool.Get();
                deltaModule.WriteReliable(tmp, receiver, key, savedInput);

                var count = tmp.positionInBits;
                Packer<PackedUInt>.Write(input, (uint)count);
                tmp.SetBitPosition(0);
                input.WriteBits(tmp, count);
            }
            else
            {
                Packer<bool>.Write(input, false);
            }
        }

        internal override void ReadInput(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            bool hasInput = default;
            Packer<bool>.Read(packer, ref hasInput);

            if (hasInput)
            {
                PackedUInt count = default;
                Packer<PackedUInt>.Read(packer, ref count);

                INPUT input = default;
                deltaModule.ReadReliable(packer, key, ref input);
                _inputHistory.Write(tick, input);
            }
            else _inputHistory.Remove(tick);
        }

        private readonly Queue<INPUT> _queuedInput = new ();

        /// <summary>
        /// Sanitize the input before using it.
        /// Use this to clamp values or prevent invalid input.
        /// </summary>
        /// <param name="input"></param>
        protected virtual void SanitizeInput(ref INPUT input) { }

        internal override void QueueInput(BitPacker packer, DeltaModule deltaModule)
        {
            bool hasInput = default;
            Packer<bool>.Read(packer, ref hasInput);

            if (hasInput)
            {
                PackedUInt count = default;
                Packer<PackedUInt>.Read(packer, ref count);

                INPUT input = default;
                deltaModule.ReadReliable(packer, key, ref input);

                var sanitizedInput = input;
                SanitizeInput(ref sanitizedInput);
                if (_queuedInput.Count >= 2)
                    _queuedInput.Clear();
                _queuedInput.Enqueue(sanitizedInput);
            }
        }
    }
}
