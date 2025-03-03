using PurrNet.Logging;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    [AddComponentMenu("PurrDiction/Predicted Transform")]
    public class PredictedTransform : PredictedIdentity<PredictedTransformState>
    {
        [SerializeField, PurrLock] private Transform _graphics;
        [SerializeField] private TransformInterpolationSettings _interpolationSettings;
        [SerializeField] private bool _characterControllerPatch = true;

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
                unityPosition = transform.position,
                unityRotation = transform.rotation
            };
        }

        protected override void GetUnityState(ref PredictedTransformState state)
        {
            if (_hasRigidbody2d)
            {
                var rot = Quaternion.Euler(0, 0, _unity2dRigidbody.rotation);
                state.SetPositionAndRotation(_unity2dRigidbody.position, rot);
            }
            else if (_hasRigidbody)
            {
                state.SetPositionAndRotation(_unityRigidbody.position, _unityRigidbody.rotation);
            }
            else state.SetPositionAndRotation(transform);
        }

        protected override void SetUnityState(PredictedTransformState state)
        {
            if (_hasRigidbody2d)
            {
                _unity2dRigidbody.position = state.unityPosition;
                _unity2dRigidbody.rotation = state.unityRotation.eulerAngles.z;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
            }
            else if (_hasRigidbody)
            {
                _unityRigidbody.position = state.unityPosition;
                _unityRigidbody.rotation = state.unityRotation;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
            }
            else if (_hasController && _characterControllerPatch)
            {
                _unityCtrler.enabled = false;
                transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
                _unityCtrler.enabled = true;
            }
            else transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
        }

        private PredictedTransformState? _viewState;
        private PredictedTransformState _oldPrediction;
        private Vector3 _accumulatedPositionError;
        private Quaternion _accumulatedRotationError = Quaternion.identity;

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
                _accumulatedPositionError += lastPrediction.unityPosition - oldPrediction.unityPosition;
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

                if (_accumulatedPositionError.sqrMagnitude > 0.01f)
                    PurrLogger.Log(_accumulatedPositionError.ToString());
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

        protected override void UpdateView(PredictedTransformState interpolatedState, PredictedTransformState? verified)
        {
            if (!_hasView)
                return;

            _graphics.SetPositionAndRotation(interpolatedState.unityPosition, interpolatedState.unityRotation);
        }
    }
}
