using BEPUutilities;

namespace BEPUik
{
    public class SingleBoneLinearMotor : SingleBoneConstraint
    {
        /// <summary>
        /// Gets or sets the target position to apply to the target bone.
        /// </summary>
        public FPVector3 TargetPosition;

        /// <summary>
        /// Gets or sets the offset in the bone's local space to the point which will be pulled towards the target position.
        /// </summary>
        public FPVector3 LocalOffset;


        public FPVector3 Offset
        {
            get { return FPQuaternion.Transform(LocalOffset, TargetBone.Orientation); }
            set { LocalOffset = FPQuaternion.Transform(value, FPQuaternion.Conjugate(TargetBone.Orientation)); }
        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
            linearJacobian = Matrix3x3.Identity;
            FPVector3 r;
            FPQuaternion.Transform(ref LocalOffset, ref TargetBone.Orientation, out r);
            Matrix3x3.CreateCrossProduct(ref r, out angularJacobian);
            //Transposing a skew symmetric matrix is equivalent to negating it.
            Matrix3x3.Transpose(ref angularJacobian, out angularJacobian);

            FPVector3 worldPosition;
            FPVector3.Add(ref TargetBone.Position, ref r, out worldPosition);

            //Error is in world space.
            FPVector3 linearError;
            FPVector3.Subtract(ref TargetPosition, ref worldPosition, out linearError);
            //This is equivalent to projecting the error onto the linear jacobian. The linear jacobian just happens to be the identity matrix!
            FPVector3.Multiply(ref linearError, errorCorrectionFactor, out velocityBias);
        }


    }
}
