using BEPUutilities;
using FixMath.NET;

namespace BEPUik
{
    /// <summary>
    /// Keeps an anchor point on one bone on a plane defined by another bone.
    /// </summary>
    public class IKPointOnPlaneJoint : IKJoint
    {
        /// <summary>
        /// Gets or sets the offset in connection A's local space from the center of mass to the anchor point of the line.
        /// </summary>
        public FPVector3 LocalPlaneAnchor;

        /// <summary>
        /// Gets or sets the direction of the line in connection A's local space.
        /// Must be unit length.
        /// </summary>
        public FPVector3 LocalPlaneNormal;

        /// <summary>
        /// Gets or sets the offset in connection B's local space from the center of mass to the anchor point which will be kept on the plane.
        /// </summary>
        public FPVector3 LocalAnchorB;

        /// <summary>
        /// Gets or sets the world space location of the line anchor attached to connection A.
        /// </summary>
        public FPVector3 PlaneAnchor
        {
            get { return ConnectionA.Position + FPQuaternion.Transform(LocalPlaneAnchor, ConnectionA.Orientation); }
            set { LocalPlaneAnchor = FPQuaternion.Transform(value - ConnectionA.Position, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the world space normal of the plane attached to connection A.
        /// Must be unit length.
        /// </summary>
        public FPVector3 PlaneNormal
        {
            get { return FPQuaternion.Transform(LocalPlaneNormal, ConnectionA.Orientation); }
            set { LocalPlaneNormal = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection B to the anchor point.
        /// </summary>
        public FPVector3 AnchorB
        {
            get { return ConnectionB.Position + FPQuaternion.Transform(LocalAnchorB, ConnectionB.Orientation); }
            set { LocalAnchorB = FPQuaternion.Transform(value - ConnectionB.Position, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }


        /// <summary>
        /// Constructs a new point on plane joint.
        /// </summary>
        /// <param name="connectionA">First bone connected by the joint.</param>
        /// <param name="connectionB">Second bone connected by the joint.</param>
        /// <param name="planeAnchor">Anchor point of the plane attached to the first bone in world space.</param>
        /// <param name="planeNormal">Normal of the plane attached to the first bone in world space. Must be unit length.</param>
        /// <param name="anchorB">Anchor point on the second bone in world space which is measured against the other connection's anchor.</param>
        public IKPointOnPlaneJoint(Bone connectionA, Bone connectionB, FPVector3 planeAnchor, FPVector3 planeNormal, FPVector3 anchorB)
            : base(connectionA, connectionB)
        {
            PlaneAnchor = planeAnchor;
            PlaneNormal = planeNormal;
            AnchorB = anchorB;
        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
            //Transform the anchors and offsets into world space.
            FPVector3 offsetA, offsetB, lineDirection;
            FPQuaternion.Transform(ref LocalPlaneAnchor, ref ConnectionA.Orientation, out offsetA);
            FPQuaternion.Transform(ref LocalPlaneNormal, ref ConnectionA.Orientation, out lineDirection);
            FPQuaternion.Transform(ref LocalAnchorB, ref ConnectionB.Orientation, out offsetB);
            FPVector3 anchorA, anchorB;
            FPVector3.Add(ref ConnectionA.Position, ref offsetA, out anchorA);
            FPVector3.Add(ref ConnectionB.Position, ref offsetB, out anchorB);

            //Compute the distance.
            FPVector3 separation;
            FPVector3.Subtract(ref anchorB, ref anchorA, out separation);
            //This entire constraint is very similar to the IKDistanceLimit, except the current distance is along an axis.
            FP currentDistance;
            FPVector3.Dot(ref separation, ref lineDirection, out currentDistance);
            velocityBias = new FPVector3(errorCorrectionFactor * currentDistance, F64.C0, F64.C0);

            //Compute jacobians
            FPVector3 angularA, angularB;
            //We can't just use the offset to anchor for A's jacobian- the 'collision' location is way out there at anchorB!
            FPVector3 rA;
            FPVector3.Subtract(ref anchorB, ref ConnectionA.Position, out rA);
            FPVector3.Cross(ref rA, ref lineDirection, out angularA);
            //linearB = -linearA, so just swap the cross product order.
            FPVector3.Cross(ref lineDirection, ref offsetB, out angularB);

            //Put all the 1x3 jacobians into a 3x3 matrix representation.
            linearJacobianA = new Matrix3x3 { M11 = lineDirection.x, M12 = lineDirection.y, M13 = lineDirection.z };
            linearJacobianB = new Matrix3x3 { M11 = -lineDirection.x, M12 = -lineDirection.y, M13 = -lineDirection.z };
            angularJacobianA = new Matrix3x3 { M11 = angularA.x, M12 = angularA.y, M13 = angularA.z };
            angularJacobianB = new Matrix3x3 { M11 = angularB.x, M12 = angularB.y, M13 = angularB.z };

        }
    }
}
