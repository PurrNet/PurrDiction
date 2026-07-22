using System;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
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
        [SerializeField] private SoftCorrectionSettings _softCorrection = SoftCorrectionSettings.Default;

        private Transform _originalGraphicsParent;

        private PredictedTransform _viewParent;
        private bool _viewParentDirty = true;
        private PredictedTransform _activeViewFrame;
        private bool _hasActiveViewFrame;
        private Vector3 _viewWorldPosition;
        private Quaternion _viewWorldRotation = Quaternion.identity;
        private uint _viewWorldPass = uint.MaxValue;
        private uint _lastViewPass = uint.MaxValue;
        private float _lastViewDeltaTime;

        public Transform graphics => _graphics;

#if UNITY_PHYSICS_3D
        private Rigidbody _unityRigidbody;
        private CharacterController _unityCtrler;
        private bool _rawRigidbodyDormant;
        private bool _rawRigidbodyKinematic;
        private CollisionDetectionMode _rawRigidbodyCollisionMode;
        private Vector3 _rawRigidbodyLinearVelocity;
        private Vector3 _rawRigidbodyAngularVelocity;
#endif
#if UNITY_PHYSICS_2D
        private Rigidbody2D _unity2dRigidbody;
        private bool _rawRigidbody2dDormant;
        private RigidbodyType2D _rawRigidbody2dBodyType;
        private Vector2 _rawRigidbody2dLinearVelocity;
        private float _rawRigidbody2dAngularVelocity;
#endif

        private bool _hasController;
        private bool _hasRigidbody2d;
        private bool _hasRigidbody;
        private bool _hasView;

        private PredictedIdentity _transformPolicyOwner;

        public override bool supportsSoftCorrection => true;

        [NonSerialized, UsedImplicitly]
        public bool updateGraphics = true;

        public override void ResetState()
        {
            base.ResetState();
            RestoreRawPhysicsDormancy();
            ClearSoftCorrection();

            _viewParent = null;
            _viewParentDirty = true;
            _activeViewFrame = null;
            _hasActiveViewFrame = false;
            _viewWorldPass = uint.MaxValue;
            _lastViewPass = uint.MaxValue;

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

        protected override PredictionPolicy ResolvePredictionPolicy()
        {
            if (TryGetTransformPolicyOwner(out var policyOwner))
                return policyOwner.ResolveDelegatedPredictionPolicy();

            return base.ResolvePredictionPolicy();
        }

        protected override PredictionPolicy ResolveSetupPredictionPolicy()
        {
            if (TryGetTransformPolicyOwner(out var policyOwner))
                return policyOwner.ResolvePredictionPolicyForSetup();

            return base.ResolveSetupPredictionPolicy();
        }

        public bool TryGetTransformPolicyOwner(out PredictedIdentity policyOwner)
        {
            if (_transformPolicyOwner && _transformPolicyOwner.controlsTransformPolicy)
            {
                policyOwner = _transformPolicyOwner;
                return true;
            }

            var identities = DisposableList<PredictedIdentity>.Create(4);
            try
            {
                GetComponents(identities.list);
                for (var i = 0; i < identities.Count; i++)
                {
                    var identity = identities[i];
                    if (!identity || identity == this || !identity.controlsTransformPolicy)
                        continue;

                    _transformPolicyOwner = identity;
                    policyOwner = identity;
                    return true;
                }
            }
            finally
            {
                identities.Dispose();
            }

            policyOwner = null;
            return false;
        }

        protected override void WriteDeltaState(BitPacker packer, in PredictedTransformState baseline, in PredictedTransformState current)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.WriteDeltaState(packer, in baseline, in current);
                    break;
                case FloatAccuracy.Medium:
                    DeltaPacker<PredictedTransformCompressedState>.Write(packer,
                        new PredictedTransformCompressedState(baseline),
                        new PredictedTransformCompressedState(current));
                    break;
                case FloatAccuracy.Low:
                    DeltaPacker<PredictedTransformHalfState>.Write(packer,
                        new PredictedTransformHalfState(baseline),
                        new PredictedTransformHalfState(current));
                    break;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        protected override void ReadDeltaState(BitPacker packer, in PredictedTransformState baseline, ref PredictedTransformState state)
        {
            switch (_floatAccuracy)
            {
                case FloatAccuracy.Purrfect:
                    base.ReadDeltaState(packer, in baseline, ref state);
                    break;
                case FloatAccuracy.Medium:
                {
                    PredictedTransformCompressedState compressedState = default;
                    DeltaPacker<PredictedTransformCompressedState>.Read(packer, new PredictedTransformCompressedState(baseline), ref compressedState);

                    state.unityPosition = compressedState.unityPosition;
                    state.unityRotation = ((Quaternion)compressedState.unityRotation).normalized;
                    break;
                }
                case FloatAccuracy.Low:
                {
                    PredictedTransformHalfState compressedState = default;
                    DeltaPacker<PredictedTransformHalfState>.Read(packer, new PredictedTransformHalfState(baseline), ref compressedState);

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

        private Vector3 _softPositionError;
        private Quaternion _softRotationError = Quaternion.identity;
        private bool _hasSoftError;

        private struct AppliedPoseTotals
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        private AppliedCorrectionRing<AppliedPoseTotals> _appliedRing;
        private Vector3 _appliedPositionTotal;
        private Quaternion _appliedRotationTotal = Quaternion.identity;

        public override void ResetInterpolation()
        {
            if (_hasActiveViewFrame && _activeViewFrame)
            {
                var state = currentState;
                WorldToViewFrame(_activeViewFrame.currentState, ref state.unityPosition, ref state.unityRotation);
                TeleportViewState(state);
            }
            else
            {
                base.ResetInterpolation();
            }

            _accumulatedPositionError = default;
            _accumulatedRotationError = Quaternion.identity;
            _viewState = null;
            _oldPrediction = default;
            _teleportNextFrame = true;
            ClearSoftCorrection();
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            _viewParentDirty = true;
        }

        protected override void OnViewInterpolationReset()
        {
            _viewParent = null;
            _viewParentDirty = true;
            _activeViewFrame = null;
            _hasActiveViewFrame = false;
            _accumulatedPositionError = default;
            _accumulatedRotationError = Quaternion.identity;
            _viewState = null;
            _oldPrediction = default;
        }

        /// <summary>
        /// Forces the view parent to be re-resolved on the next tick. Only needed when the
        /// chain of plain transforms between this object and its nearest predicted ancestor
        /// is restructured without this object's own parent changing.
        /// </summary>
        public void RefreshViewParent()
        {
            _viewParentDirty = true;
        }

        private PredictedTransform ResolveViewParent()
        {
            if (!_viewParentDirty)
            {
                if (ReferenceEquals(_viewParent, null))
                    return null;
                if (_viewParent)
                    return _viewParent;
            }

            _viewParent = null;
            bool sawUnregistered = false;
            var current = transform.parent;

            while (current != null)
            {
                if (current.TryGetComponent(out PredictedTransform candidate))
                {
                    if (ReferenceEquals(candidate.predictionManager, predictionManager))
                        _viewParent = candidate;
                    else
                        sawUnregistered = true;
                    break;
                }

                current = current.parent;
            }

            _viewParentDirty = sawUnregistered && ReferenceEquals(_viewParent, null);
            return _viewParent;
        }

        private static void WorldToViewFrame(in PredictedTransformState frame, ref Vector3 position, ref Quaternion rotation)
        {
            var inverse = Quaternion.Inverse(frame.unityRotation);
            position = inverse * (position - frame.unityPosition);
            rotation = inverse * rotation;
        }

        private static void ViewFrameToWorld(in Vector3 framePosition, in Quaternion frameRotation, ref Vector3 position, ref Quaternion rotation)
        {
            position = framePosition + frameRotation * position;
            rotation = frameRotation * rotation;
        }

        private void UpdateActiveViewFrame(ref PredictedTransformState state)
        {
            var parent = ResolveViewParent();

            if (!ReferenceEquals(parent, _activeViewFrame))
            {
                _activeViewFrame = parent;
                _hasActiveViewFrame = !ReferenceEquals(parent, null);

                _viewState = null;
                _oldPrediction = default;
                _accumulatedPositionError = default;
                _accumulatedRotationError = Quaternion.identity;

                var teleport = state;
                if (_hasActiveViewFrame)
                {
                    parent.GetLatestUnityState();
                    WorldToViewFrame(parent.currentState, ref teleport.unityPosition, ref teleport.unityRotation);
                }
                TeleportViewState(teleport);
            }

            if (_hasActiveViewFrame && _activeViewFrame)
            {
                _activeViewFrame.GetLatestUnityState();
                WorldToViewFrame(_activeViewFrame.currentState, ref state.unityPosition, ref state.unityRotation);
            }
        }

        protected override void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            ClearSoftCorrection();
        }

        internal override void SyncLocalRelevanceSideEffects(bool relevant)
        {
            if (!relevant)
                ApplyRawPhysicsDormancy();
            else
                RestoreRawPhysicsDormancy();
        }

        private void ApplyRawPhysicsDormancy()
        {
            if (isServer)
                return;

#if UNITY_PHYSICS_3D
            if (_hasRigidbody && !_rawRigidbodyDormant &&
                !_unityRigidbody.TryGetComponent<PredictedRigidbody>(out _))
            {
                _rawRigidbodyKinematic = _unityRigidbody.isKinematic;
                _rawRigidbodyCollisionMode = _unityRigidbody.collisionDetectionMode;
                _rawRigidbodyLinearVelocity = GetRawLinearVelocity(_unityRigidbody);
                _rawRigidbodyAngularVelocity = _unityRigidbody.angularVelocity;

                if (!_rawRigidbodyKinematic)
                {
                    SetRawLinearVelocity(_unityRigidbody, default);
                    _unityRigidbody.angularVelocity = default;
                }

                if (_unityRigidbody.collisionDetectionMode is CollisionDetectionMode.Continuous or
                    CollisionDetectionMode.ContinuousDynamic)
                    _unityRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                _unityRigidbody.isKinematic = true;
                _rawRigidbodyDormant = true;
            }
#endif
#if UNITY_PHYSICS_2D
            if (_hasRigidbody2d && !_rawRigidbody2dDormant &&
                !_unity2dRigidbody.TryGetComponent<PredictedRigidbody2D>(out _))
            {
                _rawRigidbody2dBodyType = _unity2dRigidbody.bodyType;
                _rawRigidbody2dLinearVelocity = GetRawLinearVelocity(_unity2dRigidbody);
                _rawRigidbody2dAngularVelocity = _unity2dRigidbody.angularVelocity;
                _unity2dRigidbody.bodyType = RigidbodyType2D.Kinematic;
                SetRawLinearVelocity(_unity2dRigidbody, default);
                _unity2dRigidbody.angularVelocity = 0f;
                _rawRigidbody2dDormant = true;
            }
#endif
        }

        private void RestoreRawPhysicsDormancy()
        {
#if UNITY_PHYSICS_3D
            if (_rawRigidbodyDormant)
            {
                if (_unityRigidbody)
                {
                    _unityRigidbody.isKinematic = _rawRigidbodyKinematic;
                    _unityRigidbody.collisionDetectionMode = _rawRigidbodyCollisionMode;
                    if (!_rawRigidbodyKinematic)
                    {
                        SetRawLinearVelocity(_unityRigidbody, _rawRigidbodyLinearVelocity);
                        _unityRigidbody.angularVelocity = _rawRigidbodyAngularVelocity;
                    }
                }

                _rawRigidbodyDormant = false;
            }
#endif
#if UNITY_PHYSICS_2D
            if (_rawRigidbody2dDormant)
            {
                if (_unity2dRigidbody)
                {
                    _unity2dRigidbody.bodyType = _rawRigidbody2dBodyType;
                    if (_rawRigidbody2dBodyType != RigidbodyType2D.Static)
                    {
                        SetRawLinearVelocity(_unity2dRigidbody, _rawRigidbody2dLinearVelocity);
                        _unity2dRigidbody.angularVelocity = _rawRigidbody2dAngularVelocity;
                    }
                }

                _rawRigidbody2dDormant = false;
            }
#endif
        }

#if UNITY_PHYSICS_2D
        private static Vector2 GetRawLinearVelocity(Rigidbody2D body)
        {
#if UNITY_6000
            return body.linearVelocity;
#else
            return body.velocity;
#endif
        }

        private static void SetRawLinearVelocity(Rigidbody2D body, Vector2 velocity)
        {
#if UNITY_6000
            body.linearVelocity = velocity;
#else
            body.velocity = velocity;
#endif
        }
#endif

#if UNITY_PHYSICS_3D
        private static Vector3 GetRawLinearVelocity(Rigidbody body)
        {
#if UNITY_6000
            return body.linearVelocity;
#else
            return body.velocity;
#endif
        }

        private static void SetRawLinearVelocity(Rigidbody body, Vector3 velocity)
        {
#if UNITY_6000
            body.linearVelocity = velocity;
#else
            body.velocity = velocity;
#endif
        }
#endif

        private void ClearSoftCorrection()
        {
            _softPositionError = default;
            _softRotationError = Quaternion.identity;
            _hasSoftError = false;
            _appliedRing?.Clear();
            _appliedPositionTotal = default;
            _appliedRotationTotal = Quaternion.identity;
        }

        internal override void SaveStateInHistory(ulong tick)
        {
            base.SaveStateInHistory(tick);

            if (isServer || !UsesSoftCorrectionTimeline())
                return;

            _appliedRing ??= new AppliedCorrectionRing<AppliedPoseTotals>(
                Mathf.Max(1, predictionManager.tickRate * 10));
            _appliedRing.Record(tick, new AppliedPoseTotals
            {
                position = _appliedPositionTotal,
                rotation = _appliedRotationTotal
            });
        }

        protected override void OnVerifiedStateReceived(ulong tick, in PredictedTransformState predicted, in PredictedTransformState verified)
        {
            var pendingPosition = verified.unityPosition - predicted.unityPosition;
            var pendingRotation = Quaternion.Inverse(predicted.unityRotation) * verified.unityRotation;

            if (_appliedRing != null && _appliedRing.TryGetBaseline(tick, out var baseline))
            {
                var appliedSincePosition = _appliedPositionTotal - baseline.position;
                var appliedSinceRotation = Quaternion.Inverse(baseline.rotation) * _appliedRotationTotal;
                pendingPosition -= appliedSincePosition;
                pendingRotation = Quaternion.Inverse(appliedSinceRotation) * pendingRotation;
            }

            _softPositionError = pendingPosition;
            _softRotationError = pendingRotation;
            _hasSoftError = pendingPosition.sqrMagnitude > 1e-8f ||
                            Quaternion.Angle(Quaternion.identity, pendingRotation) > 0.01f;
        }

        protected override void Simulate(ref PredictedTransformState state, float delta)
        {
            if (!_hasSoftError)
                return;

            float positionError = _softPositionError.magnitude;
            float rotationError = Quaternion.Angle(Quaternion.identity, _softRotationError);

            Vector3 positionStep;
            Quaternion rotationStep;

            bool snap = positionError > Mathf.Max(0f, _softCorrection.snapPositionThreshold) ||
                        rotationError > Mathf.Max(0f, _softCorrection.snapRotationThreshold);

            if (snap)
            {
                positionStep = _softPositionError;
                rotationStep = _softRotationError;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-Mathf.Max(0f, _softCorrection.correctionRate) * delta);
                positionStep = _softPositionError * blend;
                rotationStep = Quaternion.Slerp(Quaternion.identity, _softRotationError, blend);
            }

            _softPositionError -= positionStep;
            _softRotationError = Quaternion.Inverse(rotationStep) * _softRotationError;
            _appliedPositionTotal += positionStep;
            _appliedRotationTotal = (_appliedRotationTotal * rotationStep).normalized;

            if (snap && _interpolationSettings && _interpolationSettings.useInterpolation)
            {
                var viewPositionStep = positionStep;
                if (_hasActiveViewFrame && _activeViewFrame)
                    viewPositionStep = Quaternion.Inverse(_activeViewFrame.currentState.unityRotation) * viewPositionStep;

                _accumulatedPositionError += viewPositionStep;
                _accumulatedRotationError = _accumulatedRotationError * rotationStep;
            }

            if (_softPositionError.sqrMagnitude < 1e-8f &&
                Quaternion.Angle(Quaternion.identity, _softRotationError) < 0.01f)
            {
                _softPositionError = default;
                _softRotationError = Quaternion.identity;
                _hasSoftError = false;
            }

            state.SetPositionAndRotation(
                state.unityPosition + positionStep,
                (state.unityRotation * rotationStep).normalized);
            SetUnityState(state);
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
            if (_hasView && _unparentGraphics && _graphics)
                _graphics.SetParent(_originalGraphicsParent);
        }

        protected override void OnDestroy()
        {
            RestoreRawPhysicsDormancy();
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
            UpdateActiveViewFrame(ref state);

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
            var lastPrediction = state;
            var oldPrediction = _oldPrediction;
            var newView = lastView;

            if (accumulateError)
            {
                var newError = lastPrediction.unityPosition - oldPrediction.unityPosition;
                _accumulatedPositionError += newError;

                var deltaPred =
                    Quaternion.Inverse(oldPrediction.unityRotation) *
                    lastPrediction.unityRotation;

                _accumulatedRotationError *= deltaPred;
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

                float posLerp = Mathf.Clamp01(Mathf.InverseLerp(posBlend.x, posBlend.y, positionError));
                float rate = Mathf.Lerp(posRate.x, posRate.y, posLerp) * delta;
                var correction = _accumulatedPositionError * rate;

                float minThreshold = posThreshold.x * posThreshold.x;
                float corrMag = correction.sqrMagnitude;

                if (corrMag < minThreshold && positionError > posThreshold.x)
                    correction = correction.normalized * posThreshold.x;
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
                newView.unityRotation =
                    lastPrediction.unityRotation * Quaternion.Inverse(_accumulatedRotationError);

                var rotRate = rotationInterpolation.correctionRateMinMax;
                var rotBlend = rotationInterpolation.correctionBlendMinMax;
                var rotLerp = Mathf.Clamp01(Mathf.InverseLerp(rotBlend.x, rotBlend.y, rotationError));
                float rate = Mathf.Lerp(rotRate.x, rotRate.y, rotLerp) * delta;

                _accumulatedRotationError =
                    Quaternion.Slerp(_accumulatedRotationError, Quaternion.identity, rate);
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

        internal override void UpdateView(float deltaTime)
        {
            if (predictionManager == null)
            {
                base.UpdateView(deltaTime);
                return;
            }

            var pass = predictionManager.viewPassId;
            if (_lastViewPass == pass)
                return;

            _lastViewPass = pass;
            _lastViewDeltaTime = deltaTime;
            base.UpdateView(deltaTime);
        }

        internal void GetViewWorldPose(float deltaTime, out Vector3 position, out Quaternion rotation)
        {
            if (predictionManager != null)
            {
                RunUpdateView(deltaTime);

                if (_viewWorldPass == predictionManager.viewPassId)
                {
                    position = _viewWorldPosition;
                    rotation = _viewWorldRotation;
                    return;
                }
            }

            position = currentState.unityPosition;
            rotation = currentState.unityRotation;
        }

        /// <summary>
        /// Returns the interpolated world-space view pose of this transform for the current
        /// view pass, composing through predicted parents when this object is nested.
        /// </summary>
        public void GetViewWorldPose(out Vector3 position, out Quaternion rotation)
            => GetViewWorldPose(0f, out position, out rotation);

        private void ComposeViewPose(in PredictedTransformState viewState)
        {
            var position = viewState.unityPosition;
            var rotation = viewState.unityRotation;

            if (_hasActiveViewFrame && _activeViewFrame)
            {
                _activeViewFrame.GetViewWorldPose(_lastViewDeltaTime, out var framePosition, out var frameRotation);
                ViewFrameToWorld(framePosition, frameRotation, ref position, ref rotation);
            }

            _viewWorldPosition = position;
            _viewWorldRotation = rotation;

            if (predictionManager != null)
                _viewWorldPass = predictionManager.viewPassId;
        }

        protected override void UpdateView(PredictedTransformState viewState, PredictedTransformState? verified)
        {
            ComposeViewPose(in viewState);

            if (!_hasView)
                return;

            if (updateGraphics)
                _graphics.SetPositionAndRotation(_viewWorldPosition, _viewWorldRotation);
        }
    }
}
