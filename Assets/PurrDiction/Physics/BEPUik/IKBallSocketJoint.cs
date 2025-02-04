using BEPUutilities;

namespace BEPUik
{
    //Keeps the anchors from two connections near each other.
    public class IKBallSocketJoint : IKJoint
    {
        /// <summary>
        /// Gets or sets the offset in connection A's local space from the center of mass to the anchor point.
        /// </summary>
        public FPVector3 LocalOffsetA;
        /// <summary>
        /// Gets or sets the offset in connection B's local space from the center of mass to the anchor point.
        /// </summary>
        public FPVector3 LocalOffsetB;

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection A to the anchor point.
        /// </summary>
        public FPVector3 OffsetA
        {
            get { return FPQuaternion.Transform(LocalOffsetA, ConnectionA.Orientation); }
            set { LocalOffsetA = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionA.Orientation)); }
        }

        /// <summary>
        /// Gets or sets the offset in world space from the center of mass of connection B to the anchor point.
        /// </summary>
        public FPVector3 OffsetB
        {
            get { return FPQuaternion.Transform(LocalOffsetB, ConnectionB.Orientation); }
            set { LocalOffsetB = FPQuaternion.Transform(value, FPQuaternion.Conjugate(ConnectionB.Orientation)); }
        }

        /// <summary>
        /// Builds a ball socket joint.
        /// </summary>
        /// <param name="connectionA">First connection in the pair.</param>
        /// <param name="connectionB">Second connection in the pair.</param>
        /// <param name="anchor">World space anchor location used to initialize the local anchors.</param>
        public IKBallSocketJoint(Bone connectionA, Bone connectionB, FPVector3 anchor)
            : base(connectionA, connectionB)
        {
            OffsetA = anchor - ConnectionA.Position;
            OffsetB = anchor - ConnectionB.Position;
        }

        protected internal override void UpdateJacobiansAndVelocityBias()
        {
            linearJacobianA = Matrix3x3.Identity;
            //The jacobian entries are is [ La, Aa, -Lb, -Ab ] because the relative velocity is computed using A-B. So, negate B's jacobians!
            linearJacobianB = new Matrix3x3 { M11 = -1, M22 = -1, M33 = -1 };
            FPVector3 rA;
            FPQuaternion.Transform(ref LocalOffsetA, ref ConnectionA.Orientation, out rA);
            Matrix3x3.CreateCrossProduct(ref rA, out angularJacobianA);
            //Transposing a skew-symmetric matrix is equivalent to negating it.
            Matrix3x3.Transpose(ref angularJacobianA, out angularJacobianA);

            FPVector3 worldPositionA;
            FPVector3.Add(ref ConnectionA.Position, ref rA, out worldPositionA);

            FPVector3 rB;
            FPQuaternion.Transform(ref LocalOffsetB, ref ConnectionB.Orientation, out rB);
            Matrix3x3.CreateCrossProduct(ref rB, out angularJacobianB);

            FPVector3 worldPositionB;
            FPVector3.Add(ref ConnectionB.Position, ref rB, out worldPositionB);

            FPVector3 linearError;
            FPVector3.Subtract(ref worldPositionB, ref worldPositionA, out linearError);
            FPVector3.Multiply(ref linearError, errorCorrectionFactor, out velocityBias);

        }
    }
}
