using BEPUutilities;
using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class BepuRigidbody
    {
        public bool hasEntity => _entity != null;

        public FPVector3 position
        {
            get => _entity.position;
            set => _entity.position = value;
        }

        public FPQuaternion rotation
        {
            get => _entity.orientation;
            set => _entity.orientation = value;
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
            get => _entity.linearVelocity;
            set
            {
                _entity.linearVelocity = value;
            }
        }

        public FPVector3 angularVelocity
        {
            get => _entity.angularVelocity;
            set => _entity.angularVelocity = value;
        }

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
                if (_linearDrag > FP.C0)
                {
                    var dragFactor = FP.C1 - delta * _linearDrag;
                    _entity.linearVelocity *= FP.Max(FP.C0, dragFactor);
                }

                if (_angularDrag > FP.C0)
                {
                    var angularDragFactor = FP.C1 - delta * _angularDrag;
                    _entity.angularVelocity *= FP.Max(FP.C0, angularDragFactor);
                }
            }

            EnforceConstraints();
        }

        protected override void GetUnityState(ref BepuRigidbodyState state)
        {
            state.linearVelocity = _entity.linearVelocity;
            state.angularVelocity = _entity.angularVelocity;
        }

        protected override void SetUnityState(BepuRigidbodyState state)
        {
            _entity.linearVelocity = state.linearVelocity;
            _entity.angularVelocity = state.angularVelocity;
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
                    _entity.linearVelocity += force;
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
                    _entity.angularVelocity += torque;
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
                    _entity.linearVelocity += force;
                    break;
                default:
                    PurrLogger.LogException($"Force mode <b>{mode}</b> not implemented!", this);
                    break;
            }
        }
    }
}
