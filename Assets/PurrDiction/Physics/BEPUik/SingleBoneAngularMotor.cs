using BEPUutilities;
using FixMath.NET;

namespace BEPUik
{
    public class SingleBoneAngularMotor : SingleBoneConstraint
    {
        /// <summary>
        /// Gets or sets the target orientation to apply to the target bone.
        /// </summary>
        public FPQuaternion TargetOrientation;

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
            linearJacobian = new Matrix3x3();
            angularJacobian = Matrix3x3.Identity;

            //Error is in world space. It gets projected onto the jacobians later.
            FPQuaternion errorQuaternion;
            FPQuaternion.Conjugate(ref TargetBone.Orientation, out errorQuaternion);
            FPQuaternion.Multiply(ref TargetOrientation, ref errorQuaternion, out errorQuaternion);
            FP angle;
            FPVector3 angularError;
            FPQuaternion.GetAxisAngleFromQuaternion(ref errorQuaternion, out angularError, out angle);
            FPVector3.Multiply(ref angularError, angle, out angularError);

            //This is equivalent to projecting the error onto the angular jacobian. The angular jacobian just happens to be the identity matrix!
            FPVector3.Multiply(ref angularError, errorCorrectionFactor, out velocityBias);
        }


    }
}
