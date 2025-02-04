using FixMath.NET;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedTransform : PredictedIdentity<PredictedTransformState>
    {
        [SerializeField, PurrLock] private Transform _graphics;
        [SerializeField] private bool _characterControllerPatch = true;
        [SerializeField] private TransformInterpolationSettings _interpolationSettings;

        private Rigidbody _unityRigidbody;
        private Rigidbody2D _unity2dRigidbody;
        private CharacterController _unityCtrler;
        private bool _hasController;
        private bool _hasRigidbody2d;
        private bool _hasRigidbody;
        private bool _hasView;
        
        private void Awake()
        {
            _unityCtrler = GetComponent<CharacterController>();
            _unityRigidbody = GetComponent<Rigidbody>();
            _unity2dRigidbody = GetComponent<Rigidbody2D>();
            _hasController = _unityCtrler != null;
            _hasRigidbody = _unityRigidbody != null;
            _hasRigidbody2d = _unity2dRigidbody != null;
            _hasView = _graphics;
        }

        protected override PredictedTransformState GetInitialState()
        {
            return new PredictedTransformState
            {
                position = transform.position,
                rotation = transform.rotation
            };
        }

        protected override void GetUnityState(ref PredictedTransformState state)
        {
            if (_hasRigidbody)
            {
                state.position = _unityRigidbody.position;
                state.rotation = _unityRigidbody.rotation;
            }
            else transform.GetPositionAndRotation(out state.position, out state.rotation);
        }
        
        protected override void SetUnityState(PredictedTransformState state)
        {
            if (_hasRigidbody2d)
            {
                _unity2dRigidbody.position = state.position;
                _unity2dRigidbody.rotation = state.rotation.eulerAngles.z;
                transform.SetPositionAndRotation(state.position, state.rotation);
            }
            else if (_hasRigidbody)
            {
                _unityRigidbody.position = state.position;
                _unityRigidbody.rotation = state.rotation;
                transform.SetPositionAndRotation(state.position, state.rotation);
            }
            else if (_hasController && _characterControllerPatch)
            {
                _unityCtrler.enabled = false;
                transform.SetPositionAndRotation(state.position, state.rotation);
                _unityCtrler.enabled = true;
            }
            else transform.SetPositionAndRotation(state.position, state.rotation);
        }
        
        private PredictedTransformState? _viewState;
        private PredictedTransformState _oldPrediction;
        private Vector3 _accumulatedPositionError;
        private Quaternion _accumulatedRotationError = Quaternion.identity;

        protected override void ModifyRollbackViewState(ref PredictedTransformState state, FP delta, bool accumulateError)
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
                _accumulatedPositionError += lastPrediction.position - oldPrediction.position;
                _accumulatedRotationError = Quaternion.Inverse(oldPrediction.rotation) * lastPrediction.rotation * _accumulatedRotationError;
            }
            
            var positionError = _accumulatedPositionError.magnitude;
            var rotationError = Quaternion.Angle(Quaternion.identity, _accumulatedRotationError);
            
            var posThreshold = positionInterpolation.teleportThresholdMinMax;
            var rotThreshold = rotationInterpolation.teleportThresholdMinMax;
            
            var snapPos = positionError > posThreshold.y;
            var skipPos = positionError < posThreshold.x;
            
            var snapRot = rotationError > rotThreshold.y;
            var skipRot = rotationError < rotThreshold.x;
            
            if (snapPos || skipPos)
            {
                newView.position = lastPrediction.position;
                _accumulatedPositionError = default;
            }
            else
            {
                newView.position = lastPrediction.position - _accumulatedPositionError;

                var posRate = positionInterpolation.correctionRateMinMax;
                var posBlend = positionInterpolation.correctionBlendMinMax;

                // Partially correct
                float posLerp = Mathf.Clamp01(Mathf.InverseLerp(posBlend.x, posBlend.y, positionError));
                float rate = Mathf.Lerp(posRate.x, posRate.y, posLerp) * (float)delta;
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
                newView.rotation = lastPrediction.rotation;
            }
            else
            {
                newView.rotation = Quaternion.Inverse(_accumulatedRotationError) * lastPrediction.rotation;
                
                var rotRate = rotationInterpolation.correctionRateMinMax;
                var rotBlend = rotationInterpolation.correctionBlendMinMax;
                var rotLerp = Mathf.Clamp01(Mathf.InverseLerp(rotBlend.x, rotBlend.y, rotationError));
                float rate = Mathf.Lerp(rotRate.x, rotRate.y, rotLerp) * (float)delta;
                
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
                position = Vector3.Lerp(from.position, to.position, t),
                rotation = Quaternion.Slerp(from.rotation, to.rotation, t)
            };
        }

        protected override void UpdateView(PredictedTransformState interpolatedState, PredictedTransformState? verified)
        {
            if (!_hasView)
                return;
            
            _graphics.SetPositionAndRotation(interpolatedState.position, interpolatedState.rotation);
        }
    }
}
