using System;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/Predicted Transform")]
    public class PredictedTransform : PredictedIdentity<PredictedTransformState>
    {
        [SerializeField, PurrLock] private Transform _graphics;
        [SerializeField, PurrLock] private FloatAccuracy _floatAccuracy = FloatAccuracy.Medium;
        [SerializeField] private TransformInterpolationSettings _interpolationSettings;
        [Tooltip("You might want graphics to be unparented due to this gameobject being actively disabled/enabled during reconciles.")]
        [SerializeField] private bool _unparentGraphics;
        [SerializeField] private bool _characterControllerPatch = true;

        private Transform _originalGraphicsParent;

        public Transform graphics => _graphics;

#if UNITY_PHYSICS_3D
        private Rigidbody _unityRigidbody;
        private CharacterController _unityCtrler;
#endif
#if UNITY_PHYSICS_2D
        private Rigidbody2D _unity2dRigidbody;
#endif

        private bool _hasController;
        private bool _hasRigidbody2d;
        private bool _hasRigidbody;
        private bool _hasView;

        [NonSerialized, UsedImplicitly]
        public bool updateGraphics = true;

        public override void ResetState()
        {
            base.ResetState();

            if (_graphics)
                _graphics.SetPositionAndRotation(transform.position, transform.rotation);
        }

        private void Awake()
        {
#if UNITY_PHYSICS_3D
            _unityCtrler = GetComponent<CharacterController>();
            _unityRigidbody = GetComponent<Rigidbody>();
            _hasController = _unityCtrler != null;
            _hasRigidbody = _unityRigidbody != null;
#endif
#if UNITY_PHYSICS_2D
            _unity2dRigidbody = GetComponent<Rigidbody2D>();
            _hasRigidbody2d = _unity2dRigidbody != null;
#endif
            _hasView = _graphics;
        }

        protected override bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    return base.WriteDeltaState(target, packer, deltaModule);
                case FloatAccuracy.Medium:
                {
                    var key = new DeltaKey<PredictedTransformCompressedState>(id);
                    return deltaModule.WriteReliable(packer, target, key, new PredictedTransformCompressedState(currentState));
                }
                case FloatAccuracy.Low:
                {
                    var key = new DeltaKey<PredictedTransformHalfState>(id);
                    return deltaModule.WriteReliable(packer, target, key, new PredictedTransformHalfState(currentState));
                }
                default: throw new ArgumentOutOfRangeException();
            }

        }

        protected override void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, ref PredictedTransformState state)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.ReadDeltaState(packer, deltaModule, ref state);
                    break;
                case FloatAccuracy.Medium:
                {
                    var key = new DeltaKey<PredictedTransformCompressedState>(id);
                    PredictedTransformCompressedState compressedState = default;
                    deltaModule.ReadReliable(packer, key, ref compressedState);

                    state.unityPosition = compressedState.unityPosition;
                    state.unityRotation = ((Quaternion)compressedState.unityRotation).normalized;
                    break;
                }
                case FloatAccuracy.Low:
                {
                    var key = new DeltaKey<PredictedTransformHalfState>(id);
                    PredictedTransformHalfState compressedState = default;
                    deltaModule.ReadReliable(packer, key, ref compressedState);

                    state.unityPosition = compressedState.unityPosition;
                    state.unityRotation = ((Quaternion)compressedState.unityRotation).normalized;
                    break;
                }
                default: throw new ArgumentOutOfRangeException();
            }
        }

        protected override PredictedTransformState GetInitialState()
        {
            var trs = transform;
            trs.GetPositionAndRotation(out var pos, out var rot);
            return new PredictedTransformState
            {
                unityPosition = pos,
                unityRotation = rot
            };
        }

        protected override void GetUnityState(ref PredictedTransformState state)
        {
#if UNITY_PHYSICS_2D
            if (_hasRigidbody2d)
            {
                var rot = Quaternion.Euler(0, 0, _unity2dRigidbody.rotation);
                state.SetPositionAndRotation(_unity2dRigidbody.position, rot);
                return;
            }
#endif
#if UNITY_PHYSICS_3D
            if (_hasRigidbody)
            {
                state.SetPositionAndRotation(_unityRigidbody.position, _unityRigidbody.rotation);
                return;
            }
#endif
            state.SetPositionAndRotation(transform);
        }

        protected override void SetUnityState(PredictedTransformState state)
        {
#if UNITY_PHYSICS_2D
            if (_hasRigidbody2d)
            {
                _unity2dRigidbody.position = state.unityPosition;
                _unity2dRigidbody.rotation = state.unityRotation.eulerAngles.z;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
                return;
            }
#endif
#if UNITY_PHYSICS_3D
            if (_hasRigidbody)
            {
                _unityRigidbody.position = state.unityPosition;
                _unityRigidbody.rotation = state.unityRotation;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
                return;
            }
            else if (_characterControllerPatch && _hasController)
            {
                _unityCtrler.enabled = false;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
                _unityCtrler.enabled = true;
                return;
            }
#endif
            transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
        }

        private PredictedTransformState? _viewState;
        private PredictedTransformState _oldPrediction;
        private Vector3 _accumulatedPositionError;
        private Quaternion _accumulatedRotationError = Quaternion.identity;
        private bool _teleportNextFrame;

        public override void ResetInterpolation()
        {
            base.ResetInterpolation();
            _accumulatedPositionError = default;
            _accumulatedRotationError = Quaternion.identity;
            _viewState = null;
            _oldPrediction = default;
            _teleportNextFrame = true;
        }

        protected override void LateAwake()
        {
            if (_hasView && _unparentGraphics)
            {
                _originalGraphicsParent = _graphics.parent;
                _graphics.SetParent(null);
            }
        }

        protected override void OnAddedToPool()
        {
            if (_hasView && _unparentGraphics)
                _graphics.SetParent(_originalGraphicsParent);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_hasView && _unparentGraphics && _graphics)
                Destroy(_graphics.gameObject);
        }

        private void LateUpdate()
        {
            if (_teleportNextFrame)
                _teleportNextFrame = false;
        }

        protected override void ModifyRollbackViewState(ref PredictedTransformState state, float delta, bool accumulateError)
        {
            bool _smoothCorrections = _interpolationSettings && _interpolationSettings.useInterpolation;

            if (!_smoothCorrections)
                return;

            if (!_viewState.HasValue)
            {
                _viewState = state;
                _oldPrediction = state;
                return;
            }

            var positionInterpolation = _interpolationSettings.positionInterpolation;
            var rotationInterpolation = _interpolationSettings.rotationInterpolation;

            var lastView = _viewState.Value;
            var lastPrediction = currentState;
            var oldPrediction = _oldPrediction;
            var newView = lastView;

            if (accumulateError)
            {
                var newError = lastPrediction.unityPosition - oldPrediction.unityPosition;
                _accumulatedPositionError += newError;
                _accumulatedRotationError = Quaternion.Inverse(oldPrediction.unityRotation) *
                                            lastPrediction.unityRotation * _accumulatedRotationError;
            }

            var positionError = _accumulatedPositionError.magnitude;
            var rotationError = Quaternion.Angle(Quaternion.identity, _accumulatedRotationError);

            var posThreshold = positionInterpolation.teleportThresholdMinMax;
            var rotThreshold = rotationInterpolation.teleportThresholdMinMax;

            var snapPos = positionError > posThreshold.y;
            var skipPos = positionError < posThreshold.x;

            var snapRot = rotationError > rotThreshold.y;
            var skipRot = rotationError < rotThreshold.x;

            if (_teleportNextFrame)
            {
                snapPos = true;
                snapRot = true;
            }

            if (snapPos || skipPos)
            {
                newView.unityPosition = lastPrediction.unityPosition;
                _accumulatedPositionError = default;
            }
            else
            {
                newView.unityPosition = lastPrediction.unityPosition - _accumulatedPositionError;

                var posRate = positionInterpolation.correctionRateMinMax;
                var posBlend = positionInterpolation.correctionBlendMinMax;

                // Partially correct
                float posLerp = Mathf.Clamp01(Mathf.InverseLerp(posBlend.x, posBlend.y, positionError));
                float rate = Mathf.Lerp(posRate.x, posRate.y, posLerp) * delta;
                var correction = _accumulatedPositionError * rate;

                float minThreshold = posThreshold.x * posThreshold.x;
                float corrMag = correction.sqrMagnitude;

                // Clamp correction to at least posThreshold.x if we have enough error
                if (corrMag < minThreshold && positionError > minThreshold)
                    correction = correction.normalized * posThreshold.x;
                // Make sure we never exceed the total error
                else if (corrMag > positionError * positionError)
                    correction = _accumulatedPositionError;

                _accumulatedPositionError -= correction;
            }

            if (snapRot || skipRot)
            {
                _accumulatedRotationError = Quaternion.identity;
                newView.unityRotation = lastPrediction.unityRotation;
            }
            else
            {
                newView.unityRotation = Quaternion.Inverse(_accumulatedRotationError) * lastPrediction.unityRotation;

                var rotRate = rotationInterpolation.correctionRateMinMax;
                var rotBlend = rotationInterpolation.correctionBlendMinMax;
                var rotLerp = Mathf.Clamp01(Mathf.InverseLerp(rotBlend.x, rotBlend.y, rotationError));
                float rate = Mathf.Lerp(rotRate.x, rotRate.y, rotLerp) * delta;

                _accumulatedRotationError = Quaternion.Slerp(_accumulatedRotationError, Quaternion.identity, rate);
            }

            _viewState = newView;
            _oldPrediction = lastPrediction;
            state = newView;
        }

        protected override PredictedTransformState Interpolate(PredictedTransformState from, PredictedTransformState to, float t)
        {
            return new PredictedTransformState
            {
                unityPosition = Vector3.Lerp(from.unityPosition, to.unityPosition, t),
                unityRotation = Quaternion.Slerp(from.unityRotation, to.unityRotation, t)
            };
        }

        protected override void UpdateView(PredictedTransformState viewState, PredictedTransformState? verified)
        {
            if (!_hasView)
                return;

            if (updateGraphics)
                _graphics.SetPositionAndRotation(viewState.unityPosition, viewState.unityRotation);
        }
    }
}
