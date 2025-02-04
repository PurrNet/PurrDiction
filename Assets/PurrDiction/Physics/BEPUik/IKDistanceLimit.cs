using System;
using BEPUutilities;
using FixMath.NET;

namespace BEPUik
{
    /// <summary>
    /// Tries to keep the anchor points on two bones within an allowed range of distances.
    /// </summary>
    public class IKDistanceLimit : IKLimit
    {
        /// <summary>
        /// Gets or sets the offset in connection A's local space from the center of mass to the anchor point.
        /// </summary>
        public FPVector3 LocalAnchorA;
        /// <summary>
        /// Gets or sets the offset in connection B's local space from the center of mass to the anchor point.
        /// </summary>
        public FPVector3 LocalAnchorB;

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection A to the anchor point.
        /// </summary>
        public FPVector3 AnchorA
        {
            get { return ConnectionA.Position + FPQuaternion.Transform(LocalAnchorA, ConnectionA.Orientation); }
            set { LocalAnchorA = FPQuaternion.Transform(value - ConnectionA.Position, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection B to the anchor point.
        /// </summary>
        public FPVector3 AnchorB
        {
            get { return ConnectionB.Position + FPQuaternion.Transform(LocalAnchorB, ConnectionB.Orientation); }
            set { LocalAnchorB = FPQuaternion.Transform(value - ConnectionB.Position, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }

        private FP minimumDistance;
        /// <summary>
        /// Gets or sets the minimum distance that the joint connections should be kept from each other.
        /// </summary>
        public FP MinimumDistance
        {
            get { return minimumDistance; }
            set { minimumDistance = MathHelper.Max(F64.C0, value); }
        }

         private FP maximumDistance;
        /// <summary>
        /// Gets or sets the maximum distance that the joint connections should be kept from each other.
        /// </summary>
        public FP MaximumDistance
        {
            get { return maximumDistance; }
            set { maximumDistance = MathHelper.Max(F64.C0, value); }
        }

        /// <summary>
        /// Constructs a new distance joint.
        /// </summary>
        /// <param name="connectionA">First bone connected by the joint.</param>
        /// <param name="connectionB">Second bone connected by the joint.</param>
        /// <param name="anchorA">Anchor point on the first bone in world space.</param>
        /// <param name="anchorB">Anchor point on the second bone in world space.</param>
        /// <param name="minimumDistance">Minimum distance that the joint connections should be kept from each other.</param>
        /// <param name="maximumDistance">Maximum distance that the joint connections should be kept from each other.</param>
        public IKDistanceLimit(Bone connectionA, Bone connectionB, FPVector3 anchorA, FPVector3 anchorB, FP minimumDistance, FP maximumDistance)
            : base(connectionA, connectionB)
        {
            AnchorA = anchorA;
            AnchorB = anchorB;
            MinimumDistance = minimumDistance;
            MaximumDistance = maximumDistance;
        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
            //Transform the anchors and offsets into world space.
            FPVector3 offsetA, offsetB;
            FPQuaternion.Transform(ref LocalAnchorA, ref ConnectionA.Orientation, out offsetA);
            FPQuaternion.Transform(ref LocalAnchorB, ref ConnectionB.Orientation, out offsetB);
            FPVector3 anchorA, anchorB;
            FPVector3.Add(ref ConnectionA.Position, ref offsetA, out anchorA);
            FPVector3.Add(ref ConnectionB.Position, ref offsetB, out anchorB);

            //Compute the distance.
            FPVector3 separation;
            FPVector3.Subtract(ref anchorB, ref anchorA, out separation);
            FP currentDistance = separation.Length();

            //Compute jacobians
            FPVector3 linearA;
#if !WINDOWS
            linearA = new FPVector3();
#endif
            if (currentDistance > Toolbox.Epsilon)
            {
                linearA.x = separation.x / currentDistance;
                linearA.y = separation.y / currentDistance;
                linearA.z = separation.z / currentDistance;

                if (currentDistance > maximumDistance)
                {
                    //We are exceeding the maximum limit.
                    velocityBias = new FPVector3(errorCorrectionFactor * (currentDistance - maximumDistance), F64.C0, F64.C0);
                }
                else if (currentDistance < minimumDistance)
                {
                    //We are exceeding the minimum limit.
                    velocityBias = new FPVector3(errorCorrectionFactor * (minimumDistance - currentDistance), F64.C0, F64.C0);
                    //The limit can only push in one direction. Flip the jacobian!
                    FPVector3.Negate(ref linearA, out linearA);
                }
                else if (currentDistance - minimumDistance > (maximumDistance - minimumDistance) * F64.C0p5)
                {
                    //The objects are closer to hitting the maximum limit.
                    velocityBias = new FPVector3(currentDistance - maximumDistance, F64.C0, F64.C0);
                }
                else
                {
                    //The objects are closer to hitting the minimum limit.
                    velocityBias = new FPVector3(minimumDistance - currentDistance, F64.C0, F64.C0);
                    //The limit can only push in one direction. Flip the jacobian!
                    FPVector3.Negate(ref linearA, out linearA);
                }
            }
            else
            {
                velocityBias = new FPVector3();
                linearA = new FPVector3();
            }

            FPVector3 angularA, angularB;
            FPVector3.Cross(ref offsetA, ref linearA, out angularA);
            //linearB = -linearA, so just swap the cross product order.
            FPVector3.Cross(ref linearA, ref offsetB, out angularB);

            //Put all the 1x3 jacobians into a 3x3 matrix representation.
            linearJacobianA = new Matrix3x3 { M11 = linearA.x, M12 = linearA.y, M13 = linearA.z };
            linearJacobianB = new Matrix3x3 { M11 = -linearA.x, M12 = -linearA.y, M13 = -linearA.z };
            angularJacobianA = new Matrix3x3 { M11 = angularA.x, M12 = angularA.y, M13 = angularA.z };
            angularJacobianB = new Matrix3x3 { M11 = angularB.x, M12 = angularB.y, M13 = angularB.z };

        }
    }
}
