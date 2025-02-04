using BEPUphysics.Entities;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Joints
{
    /// <summary>
    /// Constrains a point on one body to be on a plane defined by another body.
    /// </summary>
    public class PointOnPlaneJoint : Joint, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private FP accumulatedImpulse;
        private FP biasVelocity;
        private FP error;

        private FPVector3 localPlaneAnchor;
        private FPVector3 localPlaneNormal;
        private FPVector3 localPointAnchor;

        private FPVector3 worldPlaneAnchor;
        private FPVector3 worldPlaneNormal;
        private FPVector3 worldPointAnchor;
        private FP negativeEffectiveMass;
        private FPVector3 rA;
        private FPVector3 rAcrossN;
        private FPVector3 rB;
        private FPVector3 rBcrossN;

        /// <summary>
        /// Constructs a new point on plane constraint.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the PlaneAnchor, PlaneNormal, and PointAnchor (or their entity-local versions).
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public PointOnPlaneJoint()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a new point on plane constraint.
        /// </summary>
        /// <param name="connectionA">Entity to which the constraint's plane is attached.</param>
        /// <param name="connectionB">Entity to which the constraint's point is attached.</param>
        /// <param name="planeAnchor">A point on the plane.</param>
        /// <param name="normal">Direction, attached to the first connected entity, defining the plane's normal</param>
        /// <param name="pointAnchor">The point to constrain to the plane, attached to the second connected object.</param>
        public PointOnPlaneJoint(Entity connectionA, Entity connectionB, FPVector3 planeAnchor, FPVector3 normal, FPVector3 pointAnchor)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            PointAnchor = pointAnchor;
            PlaneAnchor = planeAnchor;
            PlaneNormal = normal;
        }

        /// <summary>
        /// Gets or sets the plane's anchor in entity A's local space.
        /// </summary>
        public FPVector3 LocalPlaneAnchor
        {
            get { return localPlaneAnchor; }
            set
            {
                localPlaneAnchor = value;
                Matrix3x3.Transform(ref localPlaneAnchor, ref connectionA.orientationMatrix, out worldPlaneAnchor);
                FPVector3.Add(ref connectionA.position, ref worldPlaneAnchor, out worldPlaneAnchor);
            }
        }

        /// <summary>
        /// Gets or sets the plane's normal in entity A's local space.
        /// </summary>
        public FPVector3 LocalPlaneNormal
        {
            get { return localPlaneNormal; }
            set
            {
                localPlaneNormal = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localPlaneNormal, ref connectionA.orientationMatrix, out worldPlaneNormal);
            }
        }

        /// <summary>
        /// Gets or sets the point anchor in entity B's local space.
        /// </summary>
        public FPVector3 LocalPointAnchor
        {
            get { return localPointAnchor; }
            set
            {
                localPointAnchor = value;
                Matrix3x3.Transform(ref localPointAnchor, ref connectionB.orientationMatrix, out worldPointAnchor);
                FPVector3.Add(ref worldPointAnchor, ref connectionB.position, out worldPointAnchor);
            }
        }

        /// <summary>
        /// Gets the offset from A to the connection point between the entities.
        /// </summary>
        public FPVector3 OffsetA
        {
            get { return rA; }
        }

        /// <summary>
        /// Gets the offset from B to the connection point between the entities.
        /// </summary>
        public FPVector3 OffsetB
        {
            get { return rB; }
        }

        /// <summary>
        /// Gets or sets the plane anchor in world space.
        /// </summary>
        public FPVector3 PlaneAnchor
        {
            get { return worldPlaneAnchor; }
            set
            {
                worldPlaneAnchor = value;
                localPlaneAnchor = value - connectionA.position;
                Matrix3x3.TransformTranspose(ref localPlaneAnchor, ref connectionA.orientationMatrix, out localPlaneAnchor);

            }
        }

        /// <summary>
        /// Gets or sets the plane's normal in world space.
        /// </summary>
        public FPVector3 PlaneNormal
        {
            get { return worldPlaneNormal; }
            set
            {
                worldPlaneNormal = FPVector3.Normalize(value);
                Matrix3x3.TransformTranspose(ref worldPlaneNormal, ref connectionA.orientationMatrix, out localPlaneNormal);
            }
        }

        /// <summary>
        /// Gets or sets the point anchor in world space.
        /// </summary>
        public FPVector3 PointAnchor
        {
            get { return worldPointAnchor; }
            set
            {
                worldPointAnchor = value;
                localPointAnchor = value - connectionB.position;
                Matrix3x3.TransformTranspose(ref localPointAnchor, ref connectionB.orientationMatrix, out localPointAnchor);

            }
        }

        #region I1DImpulseConstraintWithError Members

        /// <summary>
        /// Gets the current relative velocity between the connected entities with respect to the constraint.
        /// </summary>
        public FP RelativeVelocity
        {
            get
            {
                FPVector3 dv;
                FPVector3 aVel, bVel;
                FPVector3.Cross(ref connectionA.angularVelocity, ref rA, out aVel);
                FPVector3.Add(ref aVel, ref connectionA.linearVelocity, out aVel);
                FPVector3.Cross(ref connectionB.angularVelocity, ref rB, out bVel);
                FPVector3.Add(ref bVel, ref connectionB.linearVelocity, out bVel);
                FPVector3.Subtract(ref aVel, ref bVel, out dv);
                FP velocityDifference;
                FPVector3.Dot(ref dv, ref worldPlaneNormal, out velocityDifference);
                return velocityDifference;
            }
        }


        /// <summary>
        /// Gets the total impulse applied by this constraint.
        /// </summary>
        public FP TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the current constraint error.
        /// </summary>
        public FP Error
        {
            get { return error; }
        }

        #endregion

        #region I1DJacobianConstraint Members

        /// <summary>
        /// Gets the linear jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobian">Linear jacobian entry for the first connected entity.</param>
        public void GetLinearJacobianA(out FPVector3 jacobian)
        {
            jacobian = worldPlaneNormal;
        }

        /// <summary>
        /// Gets the linear jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Linear jacobian entry for the second connected entity.</param>
        public void GetLinearJacobianB(out FPVector3 jacobian)
        {
            jacobian = -worldPlaneNormal;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the first connected entity.</param>
        public void GetAngularJacobianA(out FPVector3 jacobian)
        {
            jacobian = rAcrossN;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobian)
        {
            jacobian = -rBcrossN;
        }

        /// <summary>
        /// Gets the mass matrix of the constraint.
        /// </summary>
        /// <param name="outputMassMatrix">Constraint's mass matrix.</param>
        public void GetMassMatrix(out FP outputMassMatrix)
        {
            outputMassMatrix = -negativeEffectiveMass;
        }

        #endregion

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            //TODO: This could technically be faster.
            //Form the jacobian explicitly.
            //Cross cross add add subtract dot
            //vs
            //dot dot dot dot and then scalar adds
            FPVector3 dv;
            FPVector3 aVel, bVel;
            FPVector3.Cross(ref connectionA.angularVelocity, ref rA, out aVel);
            FPVector3.Add(ref aVel, ref connectionA.linearVelocity, out aVel);
            FPVector3.Cross(ref connectionB.angularVelocity, ref rB, out bVel);
            FPVector3.Add(ref bVel, ref connectionB.linearVelocity, out bVel);
            FPVector3.Subtract(ref aVel, ref bVel, out dv);
            FP velocityDifference;
            FPVector3.Dot(ref dv, ref worldPlaneNormal, out velocityDifference);
            //if(velocityDifference > 0)
            //    Debug.WriteLine("Velocity difference: " + velocityDifference);
            //Debug.WriteLine("softness velocity: " + softness * accumulatedImpulse);
            FP lambda = negativeEffectiveMass * (velocityDifference + biasVelocity + softness * accumulatedImpulse);
            accumulatedImpulse += lambda;

            FPVector3 impulse;
            FPVector3 torque;
            FPVector3.Multiply(ref worldPlaneNormal, lambda, out impulse);
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref rAcrossN, lambda, out torque);
                connectionA.ApplyLinearImpulse(ref impulse);
                connectionA.ApplyAngularImpulse(ref torque);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Negate(ref impulse, out impulse);
                FPVector3.Multiply(ref rBcrossN, lambda, out torque);
                connectionB.ApplyLinearImpulse(ref impulse);
                connectionB.ApplyAngularImpulse(ref torque);
            }

            return lambda;
        }

        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {
            Matrix3x3.Transform(ref localPlaneNormal, ref connectionA.orientationMatrix, out worldPlaneNormal);
            Matrix3x3.Transform(ref localPlaneAnchor, ref connectionA.orientationMatrix, out worldPlaneAnchor);
            FPVector3.Add(ref worldPlaneAnchor, ref connectionA.position, out worldPlaneAnchor);

            Matrix3x3.Transform(ref localPointAnchor, ref connectionB.orientationMatrix, out rB);
            FPVector3.Add(ref rB, ref connectionB.position, out worldPointAnchor);

            //Find rA and rB.
            //So find the closest point on the plane to worldPointAnchor.
            FP pointDistance, planeDistance;
            FPVector3.Dot(ref worldPointAnchor, ref worldPlaneNormal, out pointDistance);
            FPVector3.Dot(ref worldPlaneAnchor, ref worldPlaneNormal, out planeDistance);
            FP distanceChange = planeDistance - pointDistance;
            FPVector3 closestPointOnPlane;
            FPVector3.Multiply(ref worldPlaneNormal, distanceChange, out closestPointOnPlane);
            FPVector3.Add(ref closestPointOnPlane, ref worldPointAnchor, out closestPointOnPlane);

            FPVector3.Subtract(ref closestPointOnPlane, ref connectionA.position, out rA);

            FPVector3.Cross(ref rA, ref worldPlaneNormal, out rAcrossN);
            FPVector3.Cross(ref rB, ref worldPlaneNormal, out rBcrossN);
            FPVector3.Negate(ref rBcrossN, out rBcrossN);

            FPVector3 offset;
            FPVector3.Subtract(ref worldPointAnchor, ref closestPointOnPlane, out offset);
            FPVector3.Dot(ref offset, ref worldPlaneNormal, out error);
            FP errorReduction;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReduction, out softness);
            biasVelocity = MathHelper.Clamp(-errorReduction * error, -maxCorrectiveVelocity, maxCorrectiveVelocity);

            if (connectionA.IsDynamic && connectionB.IsDynamic)
            {
                FPVector3 IrACrossN, IrBCrossN;
                Matrix3x3.Transform(ref rAcrossN, ref connectionA.inertiaTensorInverse, out IrACrossN);
                Matrix3x3.Transform(ref rBcrossN, ref connectionB.inertiaTensorInverse, out IrBCrossN);
                FP angularA, angularB;
                FPVector3.Dot(ref rAcrossN, ref IrACrossN, out angularA);
                FPVector3.Dot(ref rBcrossN, ref IrBCrossN, out angularB);
                negativeEffectiveMass = connectionA.inverseMass + connectionB.inverseMass + angularA + angularB;
                negativeEffectiveMass = -1 / (negativeEffectiveMass + softness);
            }
            else if (connectionA.IsDynamic && !connectionB.IsDynamic)
            {
                FPVector3 IrACrossN;
                Matrix3x3.Transform(ref rAcrossN, ref connectionA.inertiaTensorInverse, out IrACrossN);
                FP angularA;
                FPVector3.Dot(ref rAcrossN, ref IrACrossN, out angularA);
                negativeEffectiveMass = connectionA.inverseMass + angularA;
                negativeEffectiveMass = -1 / (negativeEffectiveMass + softness);
            }
            else if (!connectionA.IsDynamic && connectionB.IsDynamic)
            {
                FPVector3 IrBCrossN;
                Matrix3x3.Transform(ref rBcrossN, ref connectionB.inertiaTensorInverse, out IrBCrossN);
                FP angularB;
                FPVector3.Dot(ref rBcrossN, ref IrBCrossN, out angularB);
                negativeEffectiveMass = connectionB.inverseMass + angularB;
                negativeEffectiveMass = -1 / (negativeEffectiveMass + softness);
            }
            else
                negativeEffectiveMass = F64.C0;


        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
        {
            //Warm Starting
            FPVector3 impulse;
            FPVector3 torque;
            FPVector3.Multiply(ref worldPlaneNormal, accumulatedImpulse, out impulse);
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref rAcrossN, accumulatedImpulse, out torque);
                connectionA.ApplyLinearImpulse(ref impulse);
                connectionA.ApplyAngularImpulse(ref torque);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Negate(ref impulse, out impulse);
                FPVector3.Multiply(ref rBcrossN, accumulatedImpulse, out torque);
                connectionB.ApplyLinearImpulse(ref impulse);
                connectionB.ApplyAngularImpulse(ref torque);
            }
        }
    }
}