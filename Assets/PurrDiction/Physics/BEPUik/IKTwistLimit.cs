using System;
using BEPUutilities;
using FixMath.NET;

namespace BEPUik
{
    /// <summary>
    /// Prevents two bones from twisting beyond a certain angle away from each other as measured by the twist between two measurement axes.
    /// </summary>
    public class IKTwistLimit : IKLimit
    {
        /// <summary>
        /// Gets or sets the axis attached to ConnectionA in its local space.
        /// Must be unit length and perpendicular to LocalMeasurementAxisA.
        /// </summary>
        public FPVector3 LocalAxisA;
        /// <summary>
        /// Gets or sets the axis attached to ConnectionB in its local space.
        /// Must be unit length and perpendicular to LocalMeasurementAxisB.
        /// </summary>
        public FPVector3 LocalAxisB;

        /// <summary>
        /// Gets or sets the measurement axis attached to connection A.
        /// Must be unit length and perpendicular to LocalAxisA.
        /// </summary>
        public FPVector3 LocalMeasurementAxisA;
        /// <summary>
        /// Gets or sets the measurement axis attached to connection B.
        /// Must be unit length and perpendicular to LocalAxisB.
        /// </summary>
        public FPVector3 LocalMeasurementAxisB;

        /// <summary>
        /// Gets or sets the axis attached to ConnectionA in world space.
        /// Must be unit length and perpendicular to MeasurementAxisA.
        /// </summary>
        public FPVector3 AxisA
        {
            get { return FPQuaternion.Transform(LocalAxisA, ConnectionA.Orientation); }
            set { LocalAxisA = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the axis attached to ConnectionB in world space.
        /// Must be unit length and perpendicular to MeasurementAxisB.
        /// </summary>
        public FPVector3 AxisB
        {
            get { return FPQuaternion.Transform(LocalAxisB, ConnectionB.Orientation); }
            set { LocalAxisB = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the measurement axis attached to ConnectionA in world space.
        /// This axis is compared against the other connection's measurement axis to determine the twist.
        /// Must be unit length and perpendicular to AxisA.
        /// </summary>
        public FPVector3 MeasurementAxisA
        {
            get { return FPQuaternion.Transform(LocalMeasurementAxisA, ConnectionA.Orientation); }
            set { LocalMeasurementAxisA = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the measurement axis attached to ConnectionB in world space.
        /// This axis is compared against the other connection's measurement axis to determine the twist.
        /// Must be unit length and perpendicular to AxisB.
        /// </summary>
        public FPVector3 MeasurementAxisB
        {
            get { return FPQuaternion.Transform(LocalMeasurementAxisB, ConnectionB.Orientation); }
            set { LocalMeasurementAxisB = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }

        private FP maximumAngle;
        /// <summary>
        /// Gets or sets the maximum angle between the two axes allowed by the constraint.
        /// </summary>
        public FP MaximumAngle
        {
            get { return maximumAngle; }
            set { maximumAngle = MathHelper.Max(F64.C0, value); }
        }

        /// <summary>
        /// Automatically computes the measurement axes for the current local axes.
        /// The current relative state of the entities will be considered 0 twist angle.
        /// </summary>
        public void ComputeMeasurementAxes()
        {
            FPVector3 axisA, axisB;
            FPQuaternion.Transform(ref LocalAxisA, ref ConnectionA.Orientation, out axisA);
            FPQuaternion.Transform(ref LocalAxisB, ref ConnectionB.Orientation, out axisB);
            //Pick an axis perpendicular to axisA to use as the measurement axis.
            FPVector3 worldMeasurementAxisA;
            FPVector3.Cross(ref Toolbox.UpVector, ref axisA, out worldMeasurementAxisA);
            FP lengthSquared = worldMeasurementAxisA.LengthSquared();
            if (lengthSquared > Toolbox.Epsilon)
            {
                FPVector3.Divide(ref worldMeasurementAxisA, FP.Sqrt(lengthSquared), out worldMeasurementAxisA);
            }
            else
            {
                //Oops! It was parallel to the up vector. Just try again with the right vector.
                FPVector3.Cross(ref Toolbox.RightVector, ref axisA, out worldMeasurementAxisA);
                worldMeasurementAxisA.Normalize();
            }
            //Attach the measurement axis to entity B.
            //'Push' A's axis onto B by taking into account the swing transform.
            FPQuaternion alignmentRotation;
            FPQuaternion.GetQuaternionBetweenNormalizedVectors(ref axisA, ref axisB, out alignmentRotation);
            FPVector3 worldMeasurementAxisB;
            FPQuaternion.Transform(ref worldMeasurementAxisA, ref alignmentRotation, out worldMeasurementAxisB);
            //Plop them on!
            MeasurementAxisA = worldMeasurementAxisA;
            MeasurementAxisB = worldMeasurementAxisB;

        }


        /// <summary>
        /// Builds a new twist limit. Prevents two bones from rotating beyond a certain angle away from each other as measured by attaching an axis to each connected bone.
        /// </summary>
        /// <param name="connectionA">First connection of the limit.</param>
        /// <param name="connectionB">Second connection of the limit.</param>
        /// <param name="axisA">Axis attached to connectionA in world space.</param>
        /// <param name="axisB">Axis attached to connectionB in world space.</param>
        /// <param name="maximumAngle">Maximum angle allowed between connectionA's axis and connectionB's axis.</param>
        public IKTwistLimit(Bone connectionA, Bone connectionB, FPVector3 axisA, FPVector3 axisB, FP maximumAngle)
            : base(connectionA, connectionB)
        {
            AxisA = axisA;
            AxisB = axisB;
            MaximumAngle = maximumAngle;

            ComputeMeasurementAxes();
        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {

            //This constraint doesn't consider linear motion.
            linearJacobianA = linearJacobianB = new Matrix3x3();

            //Compute the world axes.
            FPVector3 axisA, axisB;
            FPQuaternion.Transform(ref LocalAxisA, ref ConnectionA.Orientation, out axisA);
            FPQuaternion.Transform(ref LocalAxisB, ref ConnectionB.Orientation, out axisB);

            FPVector3 twistMeasureAxisA, twistMeasureAxisB;
            FPQuaternion.Transform(ref LocalMeasurementAxisA, ref ConnectionA.Orientation, out twistMeasureAxisA);
            FPQuaternion.Transform(ref LocalMeasurementAxisB, ref ConnectionB.Orientation, out twistMeasureAxisB);

            //Compute the shortest rotation to bring axisB into alignment with axisA.
            FPQuaternion alignmentRotation;
            FPQuaternion.GetQuaternionBetweenNormalizedVectors(ref axisB, ref axisA, out alignmentRotation);

            //Transform the measurement axis on B by the alignment quaternion.
            FPQuaternion.Transform(ref twistMeasureAxisB, ref alignmentRotation, out twistMeasureAxisB);

            //We can now compare the angle between the twist axes.
            FP angle;
            FPVector3.Dot(ref twistMeasureAxisA, ref twistMeasureAxisB, out angle);
            angle = FP.Acos(MathHelper.Clamp(angle, -1, F64.C1));

            //Compute the bias based upon the error.
            if (angle > maximumAngle)
                velocityBias = new FPVector3(errorCorrectionFactor * (angle - maximumAngle), F64.C0, F64.C0);
            else //If the constraint isn't violated, set up the velocity bias to allow a 'speculative' limit.
                velocityBias = new FPVector3(angle - maximumAngle, F64.C0, F64.C0);

            //We can't just use the axes directly as jacobians. Consider 'cranking' one object around the other.
            FPVector3 jacobian;
            FPVector3.Add(ref axisA, ref axisB, out jacobian);
            FP lengthSquared = jacobian.LengthSquared();
            if (lengthSquared > Toolbox.Epsilon)
            {
                FPVector3.Divide(ref jacobian, FP.Sqrt(lengthSquared), out jacobian);
            }
            else
            {
                //The constraint is in an invalid configuration. Just ignore it.
                jacobian = new FPVector3();
            }

            //In addition to the absolute angle value, we need to know which side of the limit we're hitting.
            //The jacobian will be negated on one side. This is because limits can only 'push' in one direction;
            //if we didn't flip the direction of the jacobian, it would be trying to push the same direction on both ends of the limit.
            //One side would end up doing nothing!
            FPVector3 cross;
            FPVector3.Cross(ref twistMeasureAxisA, ref twistMeasureAxisB, out cross);
            FP limitSide;
            FPVector3.Dot(ref cross, ref axisA, out limitSide);
            //Negate the jacobian based on what side of the limit we're on.
            if (limitSide < F64.C0)
                FPVector3.Negate(ref jacobian, out jacobian);

            angularJacobianA = new Matrix3x3 { M11 = jacobian.x, M12 = jacobian.y, M13 = jacobian.z };
            angularJacobianB = new Matrix3x3 { M11 = -jacobian.x, M12 = -jacobian.y, M13 = -jacobian.z };




        }
    }
}
