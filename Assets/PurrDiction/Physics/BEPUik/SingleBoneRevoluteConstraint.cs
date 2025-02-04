using System;
using BEPUutilities;

namespace BEPUik
{
    public class SingleBoneRevoluteConstraint : SingleBoneConstraint
    {
        private FPVector3 freeAxis;
        private FPVector3 constrainedAxis1;
        private FPVector3 constrainedAxis2;

        /// <summary>
        /// Gets or sets the direction to constrain the bone free axis to.
        /// </summary>
        public FPVector3 FreeAxis
        {
            get { return freeAxis; }
            set
            {
                freeAxis = value;
                constrainedAxis1 = FPVector3.Cross(freeAxis, FPVector3.Up);
                if (constrainedAxis1.LengthSquared() < Toolbox.Epsilon)
                {
                    constrainedAxis1 = FPVector3.Cross(freeAxis, FPVector3.Right);
                }
                constrainedAxis1.Normalize();
                constrainedAxis2 = FPVector3.Cross(freeAxis, constrainedAxis1);
            }
        }


        /// <summary>
        /// Axis of allowed rotation in the bone's local space.
        /// </summary>
        public FPVector3 BoneLocalFreeAxis;

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
 

            linearJacobian = new Matrix3x3();

            FPVector3 boneAxis;
            FPQuaternion.Transform(ref BoneLocalFreeAxis, ref TargetBone.Orientation, out boneAxis);


            angularJacobian = new Matrix3x3
            {
                M11 = constrainedAxis1.X,
                M12 = constrainedAxis1.Y,
                M13 = constrainedAxis1.Z,
                M21 = constrainedAxis2.X,
                M22 = constrainedAxis2.Y,
                M23 = constrainedAxis2.Z
            };


            FPVector3 error;
            FPVector3.Cross(ref boneAxis, ref freeAxis, out error);
            FPVector2 constraintSpaceError;
            FPVector3.Dot(ref error, ref constrainedAxis1, out constraintSpaceError.X);
            FPVector3.Dot(ref error, ref constrainedAxis2, out constraintSpaceError.Y);
            velocityBias.X = errorCorrectionFactor * constraintSpaceError.X;
            velocityBias.Y = errorCorrectionFactor * constraintSpaceError.Y;


        }


    }
}
