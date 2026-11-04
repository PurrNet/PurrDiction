using System.Reflection;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
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

        public override bool hasInput => true;

        public bool extrapolateInput {get => _extrapolateInput; set => _extrapolateInput = value; }

        private History<INPUT> _inputHistory;

        public ref INPUT currentInput => ref _currentInput;

        public override void VisitData(IPredictedDataVisitor visitor)
        {
            base.VisitData(visitor);
            visitor.VisitInput(this, ref _currentInput);
        }
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

        private INPUT? _lastInput;
        private INPUT _nextInput;

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            base.Setup(manager, world, id, owner);

            EnsureInputHistory(world);
            DisposeInputStorage();
        }

        internal override void PromoteToServer(PredictionManager world)
        {
            base.PromoteToServer(world);
            EnsureInputHistory(world);
            DisposeInputStorage();
        }

        internal override void PrewarmPredictionState(PredictionManager world)
        {
            base.PrewarmPredictionState(world);
            EnsureInputHistory(world);
        }

        private void EnsureInputHistory(PredictionManager world)
        {
            int capacity = world.isServer ? checked((int)world.verifiedHistoryWindowTicks) : world.tickRate * 5;
            // Plain inputs share one row-per-tick table per world; a history in another world's
            // table would share that world's rows.
            if (_inputHistory != null && _inputHistory.Capacity == capacity &&
                (!_inputHistory.isInTable || ReferenceEquals(_inputHistory.table, world.GetDenseStateHistory<INPUT>(capacity))))
                return;

            _inputHistory?.Clear();
            _inputHistory?.LeaveTable();
            _inputHistory = PlainValue<INPUT>.isPlain
                ? History<INPUT>.InTable(world.GetDenseStateHistory<INPUT>(capacity), capacity,
                    world.initialLocalHistoryWindowTicks, checked((int)world.verifiedHistoryWindowTicks))
                : History<INPUT>.ForTickWindow(capacity, world.initialLocalHistoryWindowTicks,
                    checked((int)world.verifiedHistoryWindowTicks));
        }

        internal override void ReleasePredictionStateForPool()
        {
            base.ReleasePredictionStateForPool();
            DisposeInputStorage();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            DisposeInputStorage();
            _inputHistory?.LeaveTable();
        }

        private void DisposeInputStorage()
        {
            _inputHistory?.Clear();
            _currentInput.Dispose();
            _currentInput = default;
            _nextInput.Dispose();
            _nextInput = default;
            _lastInput?.Dispose();
            _lastInput = null;
            if (_queuedInput.HasValue)
            {
                var queuedInput = _queuedInput.Value;
                queuedInput.Dispose();
                _queuedInput = null;
            }
        }

        internal override void OnPrepareSimulationInputs(ulong tick, float delta)
        {
            _currentInput.Dispose();
            _currentInput = GetInputForTick(tick, delta);
        }

        internal override void SimulateTick(ulong tick, float delta)
        {
            using (simulateMarker.Auto())
            {
                if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
                {
                    SimulationStart();
                    fullPredictedState.prediction.wasOnSimulationStartCalled = true;
                }

                PreSimulate(_currentInput, ref fullPredictedState.state, delta);
            }
        }

        private INPUT GetInputForTick(ulong tick, float delta)
        {
            if (IsOwner())
            {
                return !_inputHistory.TryGet(tick, out var input) ? GetDefaultInput() : PurrCopy<INPUT>.Copy(input);
            }

            switch (_extrapolateInput)
            {
                case true when _inputHistory.TryGetClosest(tick, out var extrainput, out var distanceInTicks):
                    uint maxInputs = (uint)Mathf.CeilToInt(_repeatInputFactor * 10 / (delta * 60));
                    if (distanceInTicks > maxInputs)
                    {
                        return GetDefaultInput();
                    }
                    var copy = PurrCopy<INPUT>.Copy(extrainput);
                    if (distanceInTicks > 0)
                        ModifyExtrapolatedInput(ref copy);
                    return copy;
                case false when _inputHistory.TryGet(tick, out var input):
                    return PurrCopy<INPUT>.Copy(input);
                default: return GetDefaultInput();
            }
        }

        protected virtual void LateSimulate(INPUT input, ref STATE state, float delta) {}

        internal override void LateSimulateTick(float delta)
        {
            LateSimulate(_currentInput, ref fullPredictedState.state, delta);
        }

        /// <summary>
        /// Modify the extrapolated input before it is used to simulate the state.
        /// </summary>
        protected virtual void ModifyExtrapolatedInput(ref INPUT input) { }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate)
        {
            // The server only writes inputs for its current tick. Retain the full
            // protocol window plus its preceding anchor without geometric growth.
            if (isServer)
                _inputHistory.PruneByTickWindow(tick);
            else
                PruneAcceptedInputHistory(tick);

            if (isLocal)
            {
                try
                {
                    GetFinalInput(ref _nextInput);
                    SanitizeInput(ref _nextInput);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    _nextInput = GetDefaultInput();
                }

                _lastInput?.Dispose();
                _lastInput = _nextInput;
                _inputHistory.Write(tick, Packer.Copy(_nextInput));
                _nextInput = GetDefaultInput();
                ConsumeUploadedInputBits(tick, false);
            }
            else if (isServer)
            {
                // Without a sanitizer the queued input is the decoded upload, exactly as received.
                ConsumeUploadedInputBits(tick, _queuedInput != null,
                    _queuedInput != null && _queuedInputUploaded && !sanitizesInput);
                if (_queuedInput == null)
                {
                    if (!extrapolate)
                    {
                        _lastInput?.Dispose();
                        _lastInput = GetDefaultInput();
                    }

                    _inputHistory.Write(tick, Packer.Copy(_lastInput.GetValueOrDefault()));
                    return;
                }

                var input = _queuedInput.Value;

                try
                {
                    SanitizeInput(ref input);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    input.Dispose();
                    input = GetDefaultInput();
                }

                _lastInput?.Dispose();
                _lastInput = input;
                _inputHistory.Write(tick, Packer.Copy(input));
                _queuedInput = null;
            }
        }

        protected virtual void Update()
        {
            if (isController)
                UpdateInput(ref _nextInput);
        }

        protected virtual INPUT GetDefaultInput() => default;

        private void PreSimulate(INPUT input, ref STATE state, float delta)
        {
            Simulate(input, ref state, delta);
        }

        protected abstract void Simulate(INPUT input, ref STATE state, float delta);

        protected override void Simulate(ref STATE state, float delta)
        {
            PreSimulate(_lastInput.GetValueOrDefault(), ref state, delta);
        }

        internal override bool HasInputAt(ulong tick)
        {
            return _inputHistory != null && _inputHistory.Contains(tick);
        }

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
            PruneAcceptedInputHistory(localTick);
            if (Packer<bool>.Read(packer))
            {
                var input = Packer<INPUT>.Read(packer);
                _inputHistory.Write(localTick, input);
            }
            else _inputHistory.Remove(localTick);

            TickBandwidthProfiler.OnReadInput(myType, packer.positionInBits - pos, this);
        }

        private void PruneAcceptedInputHistory(ulong tick)
        {
            ulong floor = GetAcceptedHistoryRetentionFloor(tick);
            if (floor > 0 && _inputHistory.Count > 1 && _inputHistory.OldestTick < floor)
                _inputHistory.PruneBeforeBaseline(floor);
        }

        private INPUT? _queuedInput;
        private bool _queuedInputUploaded;

        // Whether this type replaces the empty SanitizeInput, resolved once per concrete type.
        private static readonly System.Collections.Generic.Dictionary<System.Type, bool> s_sanitizers = new ();
        private int _sanitizerState;

        private bool sanitizesInput
        {
            get
            {
                if (_sanitizerState == 0)
                    _sanitizerState = ResolveSanitizer(GetType()) ? 1 : -1;
                return _sanitizerState > 0;
            }
        }

        private static bool ResolveSanitizer(System.Type type)
        {
            if (s_sanitizers.TryGetValue(type, out bool sanitizes))
                return sanitizes;
            var method = type.GetMethod(nameof(SanitizeInput),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(INPUT).MakeByRefType() }, null);
            sanitizes = method == null || method.DeclaringType != typeof(PredictedIdentity<INPUT, STATE>);
            s_sanitizers[type] = sanitizes;
            return sanitizes;
        }

        /// <summary>
        /// Sanitize the input before using it.
        /// Use this to clamp values or prevent invalid input.
        /// </summary>
        protected virtual void SanitizeInput(ref INPUT input) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender)
        {
            int pos = packer.positionInBits;
            _queuedInputUploaded = false;
            if (Packer<bool>.Read(packer))
            {
                _queuedInputUploaded = true;
                INPUT input = default;
                Packer<INPUT>.Read(packer, ref input);

                var sanitizedInput = input;

                try
                {
                    SanitizeInput(ref sanitizedInput);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    sanitizedInput.Dispose();
                    sanitizedInput = GetDefaultInput();
                }

                if (_queuedInput.HasValue)
                {
                    var queuedInput = _queuedInput.Value;
                    queuedInput.Dispose();
                }

                _queuedInput = sanitizedInput;
            }
            RecordUploadedInputBits(packer, pos);
            TickBandwidthProfiler.OnReadInput(myType, packer.positionInBits - pos, this);
        }
    }
}
