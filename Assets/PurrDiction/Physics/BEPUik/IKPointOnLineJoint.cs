using System;
using BEPUutilities;
using FixMath.NET;

namespace BEPUik
{
    /// <summary>
    /// Keeps the anchor points on two bones at the same distance.
    /// </summary>
    public class IKPointOnLineJoint : IKJoint
    {
        /// <summary>
        /// Gets or sets the offset in connection A's local space from the center of mass to the anchor point of the line.
        /// </summary>
        public FPVector3 LocalLineAnchor;

        private FPVector3 localLineDirection;
        /// <summary>
        /// Gets or sets the direction of the line in connection A's local space.
        /// Must be unit length.
        /// </summary>
        public FPVector3 LocalLineDirection
        {
            get { return localLineDirection; }
            set
            {
                localLineDirection = value;
                ComputeRestrictedAxes();
            }
        }


        /// <summary>
        /// Gets or sets the offset in connection B's local space from the center of mass to the anchor point which will be kept on the line.
        /// </summary>
        public FPVector3 LocalAnchorB;

        /// <summary>
        /// Gets or sets the world space location of the line anchor attached to connection A.
        /// </summary>
        public FPVector3 LineAnchor
        {
            get { return ConnectionA.Position + FPQuaternion.Transform(LocalLineAnchor, ConnectionA.Orientation); }
            set { LocalLineAnchor = FPQuaternion.Transform(value - ConnectionA.Position, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the world space direction of the line attached to connection A.
        /// Must be unit length.
        /// </summary>
        public FPVector3 LineDirection
        {
            get { return FPQuaternion.Transform(localLineDirection, ConnectionA.Orientation); }
            set { LocalLineDirection = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection B to the anchor point.
        /// </summary>
        public FPVector3 AnchorB
        {
            get { return ConnectionB.Position + FPQuaternion.Transform(LocalAnchorB, ConnectionB.Orientation); }
            set { LocalAnchorB = FPQuaternion.Transform(value - ConnectionB.Position, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }

        private FPVector3 localRestrictedAxis1, localRestrictedAxis2;
        void ComputeRestrictedAxes()
        {
            FPVector3 cross;
            FPVector3.Cross(ref localLineDirection, ref Toolbox.UpVector, out cross);
            FP lengthSquared = cross.LengthSquared();
            if (lengthSquared > Toolbox.Epsilon)
            {
                FPVector3.Divide(ref cross, FP.Sqrt(lengthSquared), out localRestrictedAxis1);
            }
            else
            {
                //Oops! The direction is aligned with the up vector.
                FPVector3.Cross(ref localLineDirection, ref Toolbox.RightVector, out cross);
                FPVector3.Normalize(ref cross, out localRestrictedAxis1);
            }
            //Don't need to normalize this; cross product of two unit length perpendicular vectors.
            FPVector3.Cross(ref localRestrictedAxis1, ref localLineDirection, out localRestrictedAxis2);
        }

        /// <summary>
        /// Constructs a new point on line joint.
        /// </summary>
        /// <param name="connectionA">First bone connected by the joint.</param>
        /// <param name="connectionB">Second bone connected by the joint.</param>
        /// <param name="lineAnchor">Anchor point of the line attached to the first bone in world space.</param>
        /// <param name="lineDirection">Direction of the line attached to the first bone in world space. Must be unit length.</param>
        /// <param name="anchorB">Anchor point on the second bone in world space which tries to stay on connection A's line.</param>
        public IKPointOnLineJoint(Bone connectionA, Bone connectionB, FPVector3 lineAnchor, FPVector3 lineDirection, FPVector3 anchorB)
            : base(connectionA, connectionB)
        {
            LineAnchor = lineAnchor;
            LineDirection = lineDirection;
            AnchorB = anchorB;

        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {

            //Transform local stuff into world space
            FPVector3 worldRestrictedAxis1, worldRestrictedAxis2;
            FPQuaternion.Transform(ref localRestrictedAxis1, ref ConnectionA.Orientation, out worldRestrictedAxis1);
            FPQuaternion.Transform(ref localRestrictedAxis2, ref ConnectionA.Orientation, out worldRestrictedAxis2);

            FPVector3 worldLineAnchor;
            FPQuaternion.Transform(ref LocalLineAnchor, ref ConnectionA.Orientation, out worldLineAnchor);
            FPVector3.Add(ref worldLineAnchor, ref ConnectionA.Position, out worldLineAnchor);
            FPVector3 lineDirection;
            FPQuaternion.Transform(ref localLineDirection, ref ConnectionA.Orientation, out lineDirection);

            FPVector3 rB;
            FPQuaternion.Transform(ref LocalAnchorB, ref ConnectionB.Orientation, out rB);
            FPVector3 worldPoint;
            FPVector3.Add(ref rB, ref ConnectionB.Position, out worldPoint);

            //Find the point on the line closest to the world point.
            FPVector3 offset;
            FPVector3.Subtract(ref worldPoint, ref worldLineAnchor, out offset);
            FP distanceAlongAxis;
            FPVector3.Dot(ref offset, ref lineDirection, out distanceAlongAxis);

            FPVector3 worldNearPoint;
            FPVector3.Multiply(ref lineDirection, distanceAlongAxis, out offset);
            FPVector3.Add(ref worldLineAnchor, ref offset, out worldNearPoint);
            FPVector3 rA;
            FPVector3.Subtract(ref worldNearPoint, ref ConnectionA.Position, out rA);

            //Error
            FPVector3 error3D;
            FPVector3.Subtract(ref worldPoint, ref worldNearPoint, out error3D);

            FPVector2 error;
            FPVector3.Dot(ref error3D, ref worldRestrictedAxis1, out error.x);
            FPVector3.Dot(ref error3D, ref worldRestrictedAxis2, out error.y);

            velocityBias.x = errorCorrectionFactor * error.x;
            velocityBias.y = errorCorrectionFactor * error.y;


            //Set up the jacobians
            FPVector3 angularA1, angularA2, angularB1, angularB2;
            FPVector3.Cross(ref rA, ref worldRestrictedAxis1, out angularA1);
            FPVector3.Cross(ref rA, ref worldRestrictedAxis2, out angularA2);
            FPVector3.Cross(ref worldRestrictedAxis1, ref rB, out angularB1);
            FPVector3.Cross(ref worldRestrictedAxis2, ref rB, out angularB2);

            //Put all the 1x3 jacobians into a 3x3 matrix representation.
            linearJacobianA = new Matrix3x3
            {
                M11 = worldRestrictedAxis1.x,
                M12 = worldRestrictedAxis1.y,
                M13 = worldRestrictedAxis1.z,
                M21 = worldRestrictedAxis2.x,
                M22 = worldRestrictedAxis2.y,
                M23 = worldRestrictedAxis2.z
            };
            Matrix3x3.Negate(ref linearJacobianA, out linearJacobianB);

            angularJacobianA = new Matrix3x3
            {
                M11 = angularA1.x,
                M12 = angularA1.y,
                M13 = angularA1.z,
                M21 = angularA2.x,
                M22 = angularA2.y,
                M23 = angularA2.z
            };
            angularJacobianB = new Matrix3x3
            {
                M11 = angularB1.x,
                M12 = angularB1.y,
                M13 = angularB1.z,
                M21 = angularB2.x,
                M22 = angularB2.y,
                M23 = angularB2.z
            };
        }
    }
}
