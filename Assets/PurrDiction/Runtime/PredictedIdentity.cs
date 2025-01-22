using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity : MonoBehaviour
    {
        [SerializeField] private PredictionSettings _predictionSettings = new ()
        {
            maxInterpolationQueue = 2,
            secondsToKeepInHistory = 5,
            autoIncludeTransform = true,
            positionInterpolation = new PredictedInterpolation
            {
                correctionRateMinMax = new Vector2(3.3f, 10f),
                correctionBlendMinMax = new Vector2(0.25f, 4f),
                teleportThresholdMinMax = new Vector2(0.025f, 5f)
            },
            rotationInterpolation = new PredictedInterpolation
            {
                correctionRateMinMax = new Vector2(3.3f, 10f),
                correctionBlendMinMax = new Vector2(0.1f, 0.5f),
                teleportThresholdMinMax = new Vector2(0.025f, 0.5f)
            },
            interpolate = true
        };
        
        [Tooltip("The view that will be updated with the predicted transform data.")]
        [SerializeField] internal Transform predictedView;
        
        internal bool hasView;
        internal bool _isFreshSpawn = true;

        public PredictionSettings settings
        {
            get => _predictionSettings;
            protected set => _predictionSettings = value;
        }

        public PredictionManager predictionManager { get; protected set; }

        public PlayerID? owner;

        public virtual void Setup(NetworkManager manager, PredictionManager world)
        {
            if (!_isFreshSpawn)
                return;
            
            hasView = predictedView != null;
        }

        protected virtual void OnEnable()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.RegisterInstance(this);
        }

        protected virtual void OnDisable()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
                world.UnregisterInstance(this);
        }
        
        public bool IsOwner()
        {
            return owner == predictionManager.localPlayer;
        }

        public bool IsOwner(PlayerID player)
        {
            return owner == player;
        }
        
        public bool IsOwner(PlayerID? player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID player, bool asServer)
        {
            if (owner.HasValue)
                return owner == player;
            return asServer;
        }

        internal abstract void EvaluateAndRegisterLocalInput(ulong localTick);
        
        internal abstract void SimulateTick(ulong tick, Fix64 delta);
        
        internal abstract void SimulateLocal(Fix64 delta);

        internal abstract void SimulateRemote(ulong tick, Fix64 delta);
        
        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(Fix64 delta, bool accumulateError);

        internal abstract void ResetInterpolation();
        
        internal abstract void UpdateView(float deltaTime);

        internal abstract void UpdateUnityState();
        
        public abstract void WriteLatestState(BitPacker packer);
        
        public abstract void WriteState(ulong tick, BitPacker packer);
        
        public abstract void WriteInput(ulong localTick, BitPacker input);

        public abstract void ReadState(ulong tick, BitPacker packer);
        
        public abstract void ReadInput(ulong tick, BitPacker packer);
        
        public abstract void QueueInput(BitPacker packer);

        public abstract void ClearInput();

        [UsedImplicitly]
        public virtual string GetDebugInfo(int tabs)
        {
            string tab = new string(' ', tabs * 4);
            return $"{tab}{{\n" +
                   $"{tab}    'owner': {owner} ({(IsOwner() ? "Local" : "Remote")})\n" +
                   $"{tab}}}";
        }
    }
    
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        internal struct FULL_STATE : IOptionalDispose
        {
            public STATE state;
            public PredictionState prediction;

            public FULL_STATE DeepCopy()
            {
                using var packer = BitPackerPool.Get();
                
                Packer<STATE>.Write(packer, state);
                Packer<PredictionState>.Write(packer, prediction);
                
                packer.ResetPositionAndMode(true);
                
                var data = new FULL_STATE();
                
                Packer<STATE>.Read(packer, ref data.state);
                Packer<PredictionState>.Read(packer, ref data.prediction);
                
                return data;
            }
            
            public void Dispose()
            {
                state.Dispose();
            }

            public override string ToString()
            {
                return $"{{state: {state}, prediction: {prediction}}}";
            }
        }
        
        private Interpolated<FULL_STATE> _interpolatedState;
        
        private History<FULL_STATE> _stateHistory;
        
        private bool _resetInterpolation = true;
        
        protected TickManager tickModule { get; private set; }

        internal override void ResetInterpolation()
        {
            _interpolatedState.Teleport(predictedState);
        }

        private FULL_STATE FULLInterpolate(FULL_STATE from, FULL_STATE to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            var internalState = PredictionState.Interpolate(from.prediction, to.prediction, t);
            return new FULL_STATE
            {
                state = state,
                prediction = internalState
            };
        }
        
        private Transform _transform;
        private CharacterController _unityCtrler;
        private bool _hasController;

        internal FULL_STATE predictedState;
        
        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            if (!_isFreshSpawn)
                return;
            
            base.Setup(manager, world);
            
            _unityCtrler = GetComponent<CharacterController>();
            _hasController = _unityCtrler != null;
            _transform = transform;
            
            owner = null;
            predictionManager = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            var initialState = GetInitialState();
            predictedState.state = initialState;
            UpdateUnityState();

            var copy = predictedState.DeepCopy();
            
            _interpolatedState = new Interpolated<FULL_STATE>(FULLInterpolate, (float)world.tickDelta, copy, settings.maxInterpolationQueue);
            _stateHistory = new History<FULL_STATE>(world.tickRate * settings.secondsToKeepInHistory);
            _stateHistory.Write(0, copy);
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected virtual void GetUnityState(ref STATE state) {}

        internal override void UpdateUnityState()
        {
            InternalUpdateUnityState(ref predictedState.prediction);
            GetUnityState(ref predictedState.state);
        }

        internal override void EvaluateAndRegisterLocalInput(ulong localTick) { }

        internal override void SimulateTick(ulong tick, Fix64 delta) => Simulate(delta, ref predictedState.state);

        internal override void SimulateLocal(Fix64 delta) => Simulate(delta, ref predictedState.state);

        internal override void SimulateRemote(ulong tick, Fix64 delta) => Simulate(delta, ref predictedState.state);

        private void InternalUpdateUnityState(ref PredictionState state)
        {
            state.owner = owner;
            
            if (settings.autoIncludeTransform)
            {
                _transform.GetPositionAndRotation(out var position, out var rotation);
                state.transform = new PredictionTransformState
                {
                    position = position,
                    rotation = rotation
                };
                return;
            }

            state.transform = null;
        }
        
        FULL_STATE? _latestViewState;
        
        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, predictedState.DeepCopy());
        }
        
        Vector3 _accumulatedPositionError;
        Quaternion _accumulatedRotationError = Quaternion.identity;
        
        public override void UpdateRollbackInterpolationState(Fix64 delta, bool accumulateError)
        {
            if (!_latestViewState.HasValue)
            {
                _latestViewState = predictedState.DeepCopy();
                return;
            }

            if (!settings.autoIncludeTransform)
            {
                _latestViewState?.Dispose();
                _latestViewState = predictedState.DeepCopy();
                return;
            }
            
            var latestState = predictedState.DeepCopy();

            if (!latestState.prediction.transform.HasValue ||
                !_latestViewState.Value.prediction.transform.HasValue)
            {
                _latestViewState?.Dispose();
                _latestViewState = latestState;
                return;
            }

            var oldTrs = _latestViewState.Value.prediction.transform.Value;
            var newTrs = latestState.prediction.transform.Value;

            if (accumulateError)
            {
                _accumulatedPositionError = newTrs.position - oldTrs.position;
                _accumulatedRotationError = newTrs.rotation * Quaternion.Inverse(oldTrs.rotation);
            }
            
            var positionError = _accumulatedPositionError.magnitude;
            var rotationError = Quaternion.Angle(Quaternion.identity, _accumulatedRotationError);
            
            var posThreshold = settings.positionInterpolation.teleportThresholdMinMax;
            var rotThreshold = settings.rotationInterpolation.teleportThresholdMinMax;
            
            var snapPos = positionError > posThreshold.y;
            var skipPos = positionError < posThreshold.x;
            var snapRot = rotationError > rotThreshold.y;  
            var skipRot = rotationError < rotThreshold.x;
            
            if (snapPos)
            {
                _accumulatedPositionError = default;
            }
            else if (!skipPos)
            {
                var posRate = settings.positionInterpolation.correctionRateMinMax;
                var posBlend = settings.positionInterpolation.correctionBlendMinMax;
                
                // Partially correct
                float posLerp = Mathf.Clamp01(Mathf.InverseLerp(posBlend.x, posBlend.y, positionError));
                float rate = Mathf.Lerp(posRate.x, posRate.y, posLerp) * (float)delta;
                var correction = _accumulatedPositionError * rate;
                
                float corrMag = correction.magnitude;
    
                // Clamp correction to at least posThreshold.x if we have enough error
                if (corrMag < posThreshold.x && positionError > posThreshold.x)
                    correction = correction.normalized * posThreshold.x;
                // Make sure we never exceed the total error
                else if (corrMag > positionError)
                    correction = _accumulatedPositionError;
                
                newTrs.position = oldTrs.position + correction;
                _accumulatedPositionError -= correction;
            }
            
            if (snapRot)
            {
                _accumulatedRotationError = Quaternion.identity;
            }
            else if (!skipRot)
            {
                var rotRate = settings.rotationInterpolation.correctionRateMinMax;
                var rotBlend = settings.rotationInterpolation.correctionBlendMinMax;
                var rotLerp = Mathf.Clamp01(Mathf.InverseLerp(rotBlend.x, rotBlend.y, rotationError));
                float rate = Mathf.Lerp(rotRate.x, rotRate.y, rotLerp) * (float)delta;
                
                var partialError = Quaternion.Slerp(Quaternion.identity, _accumulatedRotationError, rate);
                var correctedRot = oldTrs.rotation * partialError;
                _accumulatedRotationError = Quaternion.Inverse(partialError) * _accumulatedRotationError;
                
                newTrs.rotation = correctedRot;
            }
            
            latestState.prediction.transform = newTrs;
            
            _latestViewState?.Dispose();
            _latestViewState = latestState;
        }
        
        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(Fix64 delta, ref STATE state) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to rollback to tick {tick}, state not found.");
                return;
            }
            
            owner = state.prediction.owner;
            
            predictedState.Dispose();
            predictedState = state.DeepCopy();
            
            RollbackInternal(predictedState.prediction);
            SetUnityState(predictedState.state);
            
            if (_isFreshSpawn)
            {
                _isFreshSpawn = false;
                _resetInterpolation = false;
                _interpolatedState.Teleport(predictedState);
            }
        }
        
        private void RollbackInternal(PredictionState state)
        {
            owner = state.owner;
            var trs = state.transform;

            if (trs.HasValue && _hasController)
            {
                bool wasEnabled = _unityCtrler.enabled;
                _unityCtrler.enabled = false;
                _transform.SetPositionAndRotation(trs.Value.position, trs.Value.rotation);
                _unityCtrler.enabled = wasEnabled;
            }
            else if (trs.HasValue)
            {
                _transform.SetPositionAndRotation(trs.Value.position, trs.Value.rotation);
            }
        }
        
        protected virtual void SetUnityState(STATE state) {}

        [UsedImplicitly]
        public override void WriteState(ulong tick, BitPacker packer)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to write state at tick {tick}");
                return;
            }
            
            Packer<STATE>.Write(packer, state.state);
            Packer<PredictionState>.Write(packer, state.prediction);
        }

        public override void WriteLatestState(BitPacker packer)
        {
            Packer<STATE>.Write(packer, predictedState.state);
            Packer<PredictionState>.Write(packer, predictedState.prediction);
        }

        [UsedImplicitly]
        public override void ReadState(ulong tick, BitPacker packer)
        {
            STATE state = default;
            PredictionState prediction = default;
            Packer<STATE>.Read(packer, ref state);
            Packer<PredictionState>.Read(packer, ref prediction);
            
            _stateHistory.Write(tick, new FULL_STATE
            {
                state = state,
                prediction = prediction
            });
        }

        public override void WriteInput(ulong localTick, BitPacker input) { }

        public override void ReadInput(ulong tick, BitPacker packer) { }

        public override void QueueInput(BitPacker packer) { }

        public override void ClearInput() { }

        internal override void UpdateView(float deltaTime)
        {
            if (_interpolatedState == null)
                return;

            if (!settings.interpolate)
            {
                if (_latestViewState.HasValue)
                {
                    if (settings.autoIncludeTransform && hasView)
                        InternalUpdateView(_latestViewState.Value, _stateHistory.Count > 0 ? _stateHistory[^1] : null);
                    else UpdateView(_latestViewState.Value.state, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);
                }

                return;
            }

            if (_latestViewState.HasValue)
            {
                if (_resetInterpolation)
                {
                    _resetInterpolation = false;
                    _interpolatedState.Teleport(_latestViewState.Value);
                }
                else _interpolatedState.Add(_latestViewState.Value);
            }

            var interpolatedState = _interpolatedState.Advance(deltaTime);
            
            if (settings.autoIncludeTransform && hasView)
                InternalUpdateView(interpolatedState, _stateHistory.Count > 0 ? _stateHistory[^1] : null);
            else UpdateView(interpolatedState.state, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);
        }
        
        private void InternalUpdateView(FULL_STATE interpolatedState, FULL_STATE? verified)
        {
            var trsData = interpolatedState.prediction.transform!.Value;
            predictedView.SetPositionAndRotation(trsData.position, trsData.rotation);
            UpdateView(interpolatedState.state, verified?.state);
        }

        protected virtual void UpdateView(STATE interpolatedState, STATE? verified) {}
        
        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }
        
        public override string GetDebugInfo(int tabs)
        {
            var baseData = base.GetDebugInfo(tabs + 1);
            string tab = new string(' ', tabs * 4);
            
            return $"{tab}{{\n" +
                   $"{tab}    'base':\n{baseData}\n" +
                   $"{tab}    'state': {predictedState.state}\n" +
                   $"{tab}    'prediction': {predictedState.prediction}\n" +
                   $"{tab}}}";
        }
    }
}
