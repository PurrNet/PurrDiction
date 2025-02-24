using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class BepuRigidbody
    {
        public FPVector3 position
        {
            get => _entity.Position;
            set => _entity.Position = value;
        }

        public FPQuaternion rotation
        {
            get => _entity.Orientation;
            set => _entity.Orientation = value;
        }

        public bool isKinematic
        {
            get => _isKinematic;
            set
            {
                _isKinematic = value;
                if (_entity == null)
                    return;
                if (_isKinematic)
                    _entity.BecomeKinematic();
                else _entity.BecomeDynamic(_mass);
            }
        }

        public FPVector3 linearVelocity
        {
            get => currentState.linearVelocity;
            set => _entity.LinearVelocity = value;
        }

        public FPVector3 angularVelocity => currentState.angularVelocity;

        public FP mass => _mass;

        public FP drag
        {
            get => _linearDrag;
            set => _linearDrag = FP.Max(FP.C0, value);
        }

        public FP angularDrag
        {
            get => _angularDrag;
            set => _angularDrag = FP.Max(FP.C0, value);
        }

        protected override BepuRigidbodyState GetInitialState()
        {
            return new BepuRigidbodyState
            {
                position = transform.position.ToFPVector3(),
                orientation = transform.rotation.ToFPQuaternion(),
                linearVelocity = default,
                angularVelocity = default
            };
        }

        protected override void Simulate(ref BepuRigidbodyState state, FP delta)
        {
            if (_space == null)
                return;

            if (!_isKinematic)
            {
                state.linearVelocity += _space.ForceUpdater.Gravity * delta;

                if (_linearDrag > FP.C0)
                {
                    FP dragFactor = FP.C1 - (delta * _linearDrag);
                    state.linearVelocity *= FP.Max(FP.C0, dragFactor);
                }

                if (_angularDrag > FP.C0)
                {
                    FP angularDragFactor = FP.C1 - (delta * _angularDrag);
                    state.angularVelocity *= FP.Max(FP.C0, angularDragFactor);
                }
            }

            _entity.LinearVelocity = state.linearVelocity;
            _entity.AngularVelocity = state.angularVelocity;

            EnforceConstraints();

            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
        }

        protected override void GetUnityState(ref BepuRigidbodyState state)
        {
            state.position = _entity.Position;
            state.orientation = _entity.Orientation;
            state.linearVelocity = _entity.LinearVelocity;
            state.angularVelocity = _entity.AngularVelocity;
        }

        protected override void SetUnityState(BepuRigidbodyState state)
        {
            _entity.Position = state.position;
            _entity.Orientation = state.orientation;
            _entity.LinearVelocity = state.linearVelocity;
            _entity.AngularVelocity = state.angularVelocity;
        }

        public void AddForce(FPVector3 force, ForceMode mode = ForceMode.Force)
        {
            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyLinearImpulse(force * predictionManager.tickDelta);
                    break;
                case ForceMode.Impulse:
                    _entity.ApplyLinearImpulse(force);
                    break;
                case ForceMode.Acceleration:
                    _entity.ApplyLinearImpulse(force * mass * predictionManager.tickDelta);
                    break;
                case ForceMode.VelocityChange:
                    _entity.LinearVelocity += force;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }

        public void AddTorque(FPVector3 torque, ForceMode mode = ForceMode.Force)
        {
            if (_entity == null)
                return;

            _entity.ActivityInformation.Activate();

            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.Impulse:
                    torque *= (FP)Time.fixedDeltaTime;
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.Acceleration:
                    torque *= (FP)Time.fixedDeltaTime * _mass;
                    _entity.ApplyAngularImpulse(ref torque);
                    break;
                case ForceMode.VelocityChange:
                    _entity.AngularVelocity += torque;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }

        public void AddForceAtPosition(FPVector3 force, FPVector3 pos, ForceMode mode = ForceMode.Force)
        {
            if (_entity == null)
                return;

            _entity.ActivityInformation.Activate();

            switch (mode)
            {
                case ForceMode.Force:
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.Impulse:
                    force *= (FP)Time.fixedDeltaTime;
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.Acceleration:
                    force *= (FP)Time.fixedDeltaTime * _mass;
                    _entity.ApplyImpulse(ref force, ref pos);
                    break;
                case ForceMode.VelocityChange:
                    _entity.LinearVelocity += force;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }
    }
}
