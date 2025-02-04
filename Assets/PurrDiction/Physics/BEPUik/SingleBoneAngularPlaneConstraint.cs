using System;
using BEPUutilities;

namespace BEPUik
{
    public class SingleBoneAngularPlaneConstraint : SingleBoneConstraint
    {
        /// <summary>
        /// Gets or sets normal of the plane which the bone's axis will be constrained to..
        /// </summary>
        public FPVector3 PlaneNormal;



        /// <summary>
        /// Axis to constrain to the plane in the bone's local space.
        /// </summary>
        public FPVector3 BoneLocalAxis;

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
 

            linearJacobian = new Matrix3x3();

            FPVector3 boneAxis;
            FPQuaternion.Transform(ref BoneLocalAxis, ref TargetBone.Orientation, out boneAxis);

            FPVector3 jacobian;
            FPVector3.Cross(ref boneAxis, ref PlaneNormal, out jacobian);

            angularJacobian = new Matrix3x3
            {
                M11 = jacobian.x,
                M12 = jacobian.y,
                M13 = jacobian.z,
            };


            FPVector3.Dot(ref boneAxis, ref PlaneNormal, out velocityBias.x);
            velocityBias.x = -errorCorrectionFactor * velocityBias.x;


        }


    }
}
