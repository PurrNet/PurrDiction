using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class BepuRigidbody
    {
        [Header("Constraints")]
        [SerializeField] private bool _freezePositionX;
        [SerializeField] private bool _freezePositionY;
        [SerializeField] private bool _freezePositionZ;
        [SerializeField] private bool _freezeRotationX;
        [SerializeField] private bool _freezeRotationY;
        [SerializeField] private bool _freezeRotationZ;

        private FPVector3 _lockedPosition;
        private FPQuaternion _lockedRotation;

        public bool freezePositionX
        {
            get => _freezePositionX;
            set
            {
                _freezePositionX = value;
                if (value) _lockedPosition.x = _entity?.position.x ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezePositionY
        {
            get => _freezePositionY;
            set
            {
                _freezePositionY = value;
                if (value) _lockedPosition.y = _entity?.position.y ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezePositionZ
        {
            get => _freezePositionZ;
            set
            {
                _freezePositionZ = value;
                if (value) _lockedPosition.z = _entity?.position.z ?? FP.C0;
                UpdateConstraints();
            }
        }

        public bool freezeRotationX
        {
            get => _freezeRotationX;
            set
            {
                _freezeRotationX = value;
                if (value) _lockedRotation = _entity?.orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        public bool freezeRotationY
        {
            get => _freezeRotationY;
            set
            {
                _freezeRotationY = value;
                if (value) _lockedRotation = _entity?.orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        public bool freezeRotationZ
        {
            get => _freezeRotationZ;
            set
            {
                _freezeRotationZ = value;
                if (value) _lockedRotation = _entity?.orientation ?? FPQuaternion.Identity;
                UpdateConstraints();
            }
        }

        private void SetupConstraints()
        {
            _lockedPosition = _entity.position;
            _lockedRotation = _entity.orientation;
            UpdateConstraints();
        }

        private void UpdateConstraints()
        {
            if (_entity == null)
                return;

            _lockedPosition = _entity.position;
            _lockedRotation = _entity.orientation;

            EnforceConstraints();
        }

        private void EnforceConstraints()
        {
            if (_entity == null) return;

            var currentPos = _entity.position;
            var currentRot = _entity.orientation;

            if (_freezePositionX) currentPos.x = _lockedPosition.x;
            if (_freezePositionY) currentPos.y = _lockedPosition.y;
            if (_freezePositionZ) currentPos.z = _lockedPosition.z;

            if (_freezeRotationX || _freezeRotationY || _freezeRotationZ)
            {
                var eulerAngles = currentRot.ToEuler();
                var lockedEulerAngles = _lockedRotation.ToEuler();

                if (_freezeRotationX) eulerAngles.x = lockedEulerAngles.x;
                if (_freezeRotationY) eulerAngles.y = lockedEulerAngles.y;
                if (_freezeRotationZ) eulerAngles.z = lockedEulerAngles.z;

                currentRot = FPQuaternion.CreateFromEuler(eulerAngles);
            }

            _entity.position = currentPos;
            _entity.orientation = currentRot;

            var linearVel = _entity.linearVelocity;
            var angularVel = _entity.angularVelocity;

            if (_freezePositionX) linearVel.x = FP.C0;
            if (_freezePositionY) linearVel.y = FP.C0;
            if (_freezePositionZ) linearVel.z = FP.C0;

            if (_freezeRotationX) angularVel.x = FP.C0;
            if (_freezeRotationY) angularVel.y = FP.C0;
            if (_freezeRotationZ) angularVel.z = FP.C0;

            _entity.linearVelocity = linearVel;
            _entity.angularVelocity = angularVel;
        }
    }
}
