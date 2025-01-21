using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictionTransformState : IPredictedData<PredictionTransformState>
    {
        public Vector3 position;
        public Quaternion rotation;
        
        public override string ToString()
        {
            return $"(position: {position}, rotation: {rotation})";
        }
    }
    
    public struct PredictionState : IPredictedData<PredictionState>
    {
        public PlayerID? owner;
        public PredictionTransformState? transform;
        
        public PredictionState Add(PredictionState a, PredictionState b)
        {
            a.transform = a.transform.HasValue && b.transform.HasValue
                ? new PredictionTransformState
                {
                    position = a.transform.Value.position + b.transform.Value.position,
                    rotation = a.transform.Value.rotation * b.transform.Value.rotation
                }
                : null;
            return a;
        }

        public PredictionState Negate(PredictionState a)
        {
            a.transform = a.transform.HasValue
                ? new PredictionTransformState
                {
                    position = -a.transform.Value.position,
                    rotation = Quaternion.Inverse(a.transform.Value.rotation)
                }
                : null;
            return a;
        }

        public PredictionState Scale(PredictionState a, float b)
        {
            a.transform = a.transform.HasValue
                ? new PredictionTransformState
                {
                    position = a.transform.Value.position * b,
                    rotation = Quaternion.Slerp(Quaternion.identity, a.transform.Value.rotation, b)
                }
                : null;
            
            return a;
        }

        public override string ToString()
        {
            return $"(owner: {owner}, transform: {transform})";
        }
    }
    
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
                correctionBlendMinMax = new Vector2(0.2f, 1f),
                teleportThresholdMinMax = new Vector2(0.025f, 2f)
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

        public PredictionSettings settings
        {
            get => _predictionSettings;
            protected set => _predictionSettings = value;
        }

        public PredictionManager predictionManager { get; protected set; }

        public PlayerID? owner;

        public virtual void Setup(NetworkManager manager, PredictionManager world)
        {
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

        internal abstract void SimulateRemote(Fix64 delta);
        
        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        internal abstract void UpdateInterpolationState();

        internal abstract void ResetInterpolation();
        
        internal abstract void UpdateView(float deltaTime);
        
        public abstract void WriteLatestState(BitPacker packer);
        
        public abstract void WriteState(ulong tick, BitPacker packer);
        
        public abstract void WriteInput(ulong localTick, BitPacker input);

        public abstract void ReadState(ulong tick, BitPacker packer);
        
        public abstract void ReadInput(ulong tick, BitPacker packer);
        
        public abstract void QueueInput(BitPacker packer);

        public abstract void ClearInput();
    }
    
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        struct FULL_STATE : IOptionalDispose
        {
            public STATE state;
            public PredictionState prediction;
            
            public void Dispose()
            {
                state.Dispose();
            }
        }
        
        private Interpolated<FULL_STATE> _interpolatedState;
        
        private History<FULL_STATE> _stateHistory;
        
        private bool _resetInterpolation;
        
        protected STATE predictedState;

        protected STATE? verifiedState;
        
        protected TickManager tickModule { get; private set; }

        internal override void ResetInterpolation()
        {
            _resetInterpolation = true;
        }

        private FULL_STATE FULLInterpolate(FULL_STATE from, FULL_STATE to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            var internalState = Interpolate(from.prediction, to.prediction, t);
            
            return new FULL_STATE
            {
                state = state,
                prediction = internalState
            };
        }
        
        private Transform _transform;
        private CharacterController _unityCtrler;
        private bool _hasController;
        
        public override void Setup(NetworkManager manager, PredictionManager world)
        {
            base.Setup(manager, world);
            
            _unityCtrler = GetComponent<CharacterController>();
            _hasController = _unityCtrler != null;
            _transform = transform;
            
            owner = null;
            predictionManager = world;
            tickModule = manager.tickModule;
            
            if (tickModule == null)
                return;
            
            predictedState = UpdateUnityState();
            
            var predicted = new FULL_STATE
            {
                state = predictedState,
                prediction = new PredictionState
                {
                    owner = owner,
                    transform = settings.autoIncludeTransform ? new PredictionTransformState
                    {
                        position = _transform.position,
                        rotation = _transform.rotation
                    } : null
                }
            };
            
            _interpolatedState = new Interpolated<FULL_STATE>(FULLInterpolate, (float)world.tickDelta, predicted, settings.maxInterpolationQueue);
            _stateHistory = new History<FULL_STATE>(world.tickRate * settings.secondsToKeepInHistory);
            _stateHistory.Write(0, predicted);
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected abstract STATE UpdateUnityState();

        internal override void EvaluateAndRegisterLocalInput(ulong localTick) { }

        internal override void SimulateTick(ulong tick, Fix64 delta) => Simulate(delta);

        internal override void SimulateLocal(Fix64 delta) => Simulate(delta);

        internal override void SimulateRemote(Fix64 delta) => Simulate(delta);

        FULL_STATE GetCurrentFullState()
        {
            if (settings.autoIncludeTransform)
            {
                _transform.GetPositionAndRotation(out var position, out var rotation);
                return new FULL_STATE
                {
                    state = UpdateUnityState(),
                    prediction = new PredictionState
                    {
                        owner = owner,
                        transform = new PredictionTransformState
                        {
                            position = position,
                            rotation = rotation
                        }
                    }
                };
            }
            
            return new FULL_STATE
            {
                state = UpdateUnityState(),
                prediction = new PredictionState
                {
                    owner = owner,
                    transform = null
                }
            };
        }
        
        FULL_STATE? _lastState;
        
        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.Write(tick, GetCurrentFullState());
        }
        
        internal override void UpdateInterpolationState()
        {
            _lastState?.Dispose();
            _lastState = GetCurrentFullState();
        }

        protected abstract void Simulate(Fix64 delta);

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.Read(tick, out var state))
            {
                PurrLogger.LogError($"Failed to rollback to tick {tick}, state not found.");
                return;
            }
            
            verifiedState = state.state;
            RollbackInternal(state.prediction);
            RollbackUnityState(state.state);
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
        
        protected abstract void RollbackUnityState(STATE state);

        [UsedImplicitly]
        public override void WriteState(ulong tick, BitPacker packer)
        {
            if (_stateHistory.Read(tick, out var state))
            {
                Packer<STATE>.Write(packer, state.state);
                Packer<PredictionState>.Write(packer, state.prediction);
            }
            else PurrLogger.LogError($"Failed to write state at tick {tick}");
        }

        public override void WriteLatestState(BitPacker packer)
        {
            var state = GetCurrentFullState();
            Packer<STATE>.Write(packer, state.state);
            Packer<PredictionState>.Write(packer, state.prediction);
            state.Dispose();
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
                if (_lastState.HasValue)
                {
                    if (settings.autoIncludeTransform && hasView)
                        InternalUpdateView(_lastState.Value, _stateHistory.Count > 0 ? _stateHistory[^1] : null);
                    else UpdateView(_lastState.Value.state, _stateHistory.Count > 0 ? _stateHistory[^1].state : null);

                    _lastState.Value.Dispose();
                    _lastState = null;
                }

                return;
            }

            if (_lastState.HasValue)
            {
                if (_resetInterpolation)
                {
                    _resetInterpolation = false;
                    _interpolatedState.Teleport(_lastState.Value);
                }
                else _interpolatedState.Add(_lastState.Value);

                _lastState.Value.Dispose();
                _lastState = null;
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
        
        static PredictionState Interpolate(PredictionState from, PredictionState to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            var result = from.Add(from, scaled);
            return result;
        }
    }
}
