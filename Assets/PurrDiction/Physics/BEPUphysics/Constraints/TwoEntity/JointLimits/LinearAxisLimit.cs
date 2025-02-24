using System;
using BEPUphysics.Entities;
 
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.JointLimits
{
    /// <summary>
    /// Constrains the distance along an axis between anchor points attached to two entities.
    /// </summary>
    public class LinearAxisLimit : JointLimit, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private FP accumulatedImpulse;
        private FP biasVelocity;
        private FPVector3 jAngularA, jAngularB;
        private FPVector3 jLinearA, jLinearB;
        private FPVector3 localAnchorA;
        private FPVector3 localAnchorB;
        private FP massMatrix;
        private FP error;
        private FPVector3 localAxis;
        private FP maximum;
        private FP minimum;
        private FPVector3 worldAxis;
        private FPVector3 rA; //Jacobian entry for entity A.
        private FP unadjustedError;
        private FPVector3 worldAnchorA;
        private FPVector3 worldAnchorB;
        private FPVector3 worldOffsetA, worldOffsetB;

        /// <summary>
        /// Constructs a constraint which tries to keep anchors on two entities within a certain distance of each other along an axis.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the AnchorA, AnchorB, and Axis (or their entity-local versions),
        /// and the Minimum and Maximum.
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public LinearAxisLimit()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a constraint which tries to keep anchors on two entities within a certain distance of each other along an axis.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        /// <param name="anchorA">World space point to attach to connection A that will be constrained.</param>
        /// <param name="anchorB">World space point to attach to connection B that will be constrained.</param>
        /// <param name="axis">Limited axis in world space to attach to connection A.</param>
        /// <param name="minimum">Minimum allowed position along the axis.</param>
        /// <param name="maximum">Maximum allowed position along the axis.</param>
        public LinearAxisLimit(Entity connectionA, Entity connectionB, FPVector3 anchorA, FPVector3 anchorB, FPVector3 axis, FP minimum, FP maximum)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
            AnchorA = anchorA;
            AnchorB = anchorB;
            Axis = axis;
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>
        /// Gets or sets the anchor point attached to entity A in world space.
        /// </summary>
        public FPVector3 AnchorA
        {
            get { return worldAnchorA; }
            set
            {
                worldAnchorA = value;
                worldOffsetA = worldAnchorA - connectionA._position;
                Matrix3x3.TransformTranspose(ref worldOffsetA, ref connectionA._orientationMatrix, out localAnchorA);
            }
        }

        /// <summary>
        /// Gets or sets the anchor point attached to entity A in world space.
        /// </summary>
        public FPVector3 AnchorB
        {
            get { return worldAnchorB; }
            set
            {
                worldAnchorB = value;
                worldOffsetB = worldAnchorB - connectionB._position;
                Matrix3x3.TransformTranspose(ref worldOffsetB, ref connectionB._orientationMatrix, out localAnchorB);
            }
        }

        /// <summary>
        /// Gets or sets the limited axis in world space.
        /// </summary>
        public FPVector3 Axis
        {
            get { return worldAxis; }
            set
            {
                worldAxis = FPVector3.Normalize(value);
                Matrix3x3.TransformTranspose(ref worldAxis, ref connectionA._orientationMatrix, out localAxis);
            }
        }

        /// <summary>
        /// Gets or sets the limited axis in the local space of connection A.
        /// </summary>
        public FPVector3 LocalAxis
        {
            get { return localAxis; }
            set
            {
                localAxis = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localAxis, ref connectionA._orientationMatrix, out worldAxis);
            }
        }

        /// <summary>
        /// Gets or sets the offset from the first entity's center of mass to the anchor point in its local space.
        /// </summary>
        public FPVector3 LocalOffsetA
        {
            get { return localAnchorA; }
            set
            {
                localAnchorA = value;
                Matrix3x3.Transform(ref localAnchorA, ref connectionA._orientationMatrix, out worldOffsetA);
                worldAnchorA = connectionA._position + worldOffsetA;
            }
        }

        /// <summary>
        /// Gets or sets the offset from the second entity's center of mass to the anchor point in its local space.
        /// </summary>
        public FPVector3 LocalOffsetB
        {
            get { return localAnchorB; }
            set
            {
                localAnchorB = value;
                Matrix3x3.Transform(ref localAnchorB, ref connectionB._orientationMatrix, out worldOffsetB);
                worldAnchorB = connectionB._position + worldOffsetB;
            }
        }

        /// <summary>
        /// Gets or sets the maximum allowed distance along the axis.
        /// </summary>
        public FP Maximum
        {
            get { return maximum; }
            set
            {
                maximum = value;
                minimum = MathHelper.Min(minimum, maximum);
            }
        }

        /// <summary>
        /// Gets or sets the minimum allowed distance along the axis.
        /// </summary>
        public FP Minimum
        {
            get { return minimum; }
            set
            {
                minimum = value;
                maximum = MathHelper.Max(minimum, maximum);
            }
        }

        /// <summary>
        /// Gets or sets the offset from the first entity's center of mass to the anchor point in world space.
        /// </summary>
        public FPVector3 OffsetA
        {
            get { return worldOffsetA; }
            set
            {
                worldOffsetA = value;
                worldAnchorA = connectionA._position + worldOffsetA;
                Matrix3x3.TransformTranspose(ref worldOffsetA, ref connectionA._orientationMatrix, out localAnchorA);
            }
        }

        /// <summary>
        /// Gets or sets the offset from the second entity's center of mass to the anchor point in world space.
        /// </summary>
        public FPVector3 OffsetB
        {
            get { return worldOffsetB; }
            set
            {
                worldOffsetB = value;
                worldAnchorB = connectionB._position + worldOffsetB;
                Matrix3x3.TransformTranspose(ref worldOffsetB, ref connectionB._orientationMatrix, out localAnchorB);
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
                if (isLimitActive)
                {
                    FP lambda, dot;
                    FPVector3.Dot(ref jLinearA, ref connectionA._linearVelocity, out lambda);
                    FPVector3.Dot(ref jAngularA, ref connectionA._angularVelocity, out dot);
                    lambda += dot;
                    FPVector3.Dot(ref jLinearB, ref connectionB._linearVelocity, out dot);
                    lambda += dot;
                    FPVector3.Dot(ref jAngularB, ref connectionB._angularVelocity, out dot);
                    lambda += dot;
                    return lambda;
                }
                return F64.C0;
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

        //Jacobians

        #region I1DJacobianConstraint Members

        /// <summary>
        /// Gets the linear jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobian">Linear jacobian entry for the first connected entity.</param>
        public void GetLinearJacobianA(out FPVector3 jacobian)
        {
            jacobian = jLinearA;
        }

        /// <summary>
        /// Gets the linear jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Linear jacobian entry for the second connected entity.</param>
        public void GetLinearJacobianB(out FPVector3 jacobian)
        {
            jacobian = jLinearB;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the first connected entity.</param>
        public void GetAngularJacobianA(out FPVector3 jacobian)
        {
            jacobian = jAngularA;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobian)
        {
            jacobian = jAngularB;
        }

        /// <summary>
        /// Gets the mass matrix of the constraint.
        /// </summary>
        /// <param name="outputMassMatrix">Constraint's mass matrix.</param>
        public void GetMassMatrix(out FP outputMassMatrix)
        {
            outputMassMatrix = massMatrix;
        }

        #endregion

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            //Compute the current relative velocity.
            FP lambda, dot;
            FPVector3.Dot(ref jLinearA, ref connectionA._linearVelocity, out lambda);
            FPVector3.Dot(ref jAngularA, ref connectionA._angularVelocity, out dot);
            lambda += dot;
            FPVector3.Dot(ref jLinearB, ref connectionB._linearVelocity, out dot);
            lambda += dot;
            FPVector3.Dot(ref jAngularB, ref connectionB._angularVelocity, out dot);
            lambda += dot;

            //Add in the constraint space bias velocity
            lambda = -lambda + biasVelocity - softness * accumulatedImpulse;

            //Transform to an impulse
            lambda *= massMatrix;

            //Clamp accumulated impulse (can't go negative)
            FP previousAccumulatedImpulse = accumulatedImpulse;
            if (unadjustedError < F64.C0)
                accumulatedImpulse = MathHelper.Min(accumulatedImpulse + lambda, F64.C0);
            else
                accumulatedImpulse = MathHelper.Max(accumulatedImpulse + lambda, F64.C0);
            lambda = accumulatedImpulse - previousAccumulatedImpulse;

            //Apply the impulse
            FPVector3 impulse;
            if (connectionA._isDynamic)
            {
                FPVector3.Multiply(ref jLinearA, lambda, out impulse);
                connectionA.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularA, lambda, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Multiply(ref jLinearB, lambda, out impulse);
                connectionB.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularB, lambda, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }

            return (FP.Abs(lambda));
        }

        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {
            //Compute the 'pre'-jacobians
            Matrix3x3.Transform(ref localAnchorA, ref connectionA._orientationMatrix, out worldOffsetA);
            Matrix3x3.Transform(ref localAnchorB, ref connectionB._orientationMatrix, out worldOffsetB);
            FPVector3.Add(ref worldOffsetA, ref connectionA._position, out worldAnchorA);
            FPVector3.Add(ref worldOffsetB, ref connectionB._position, out worldAnchorB);
            FPVector3.Subtract(ref worldAnchorB, ref connectionA._position, out rA);
            Matrix3x3.Transform(ref localAxis, ref connectionA._orientationMatrix, out worldAxis);

            //Compute error
#if !WINDOWS
            FPVector3 separation = new FPVector3();
#else
            Vector3 separation;
#endif
            separation.x = worldAnchorB.x - worldAnchorA.x;
            separation.y = worldAnchorB.y - worldAnchorA.y;
            separation.z = worldAnchorB.z - worldAnchorA.z;

            FPVector3.Dot(ref separation, ref worldAxis, out unadjustedError);

            //Compute error
            if (unadjustedError < minimum)
                unadjustedError = minimum - unadjustedError;
            else if (unadjustedError > maximum)
                unadjustedError = maximum - unadjustedError;
            else
            {
                unadjustedError = F64.C0;
                isActiveInSolver = false;
                accumulatedImpulse = F64.C0;
                isLimitActive = false;
                return;
            }
            isLimitActive = true;

            unadjustedError = -unadjustedError;
            //Adjust Error
            if (unadjustedError > F64.C0)
                error = MathHelper.Max(F64.C0, unadjustedError - margin);
            else if (unadjustedError < F64.C0)
                error = MathHelper.Min(F64.C0, unadjustedError + margin);

            //Compute jacobians
            jLinearA = worldAxis;
            jLinearB.x = -jLinearA.x;
            jLinearB.y = -jLinearA.y;
            jLinearB.z = -jLinearA.z;
            FPVector3.Cross(ref rA, ref jLinearA, out jAngularA);
            FPVector3.Cross(ref worldOffsetB, ref jLinearB, out jAngularB);

            //Compute bias
            FP errorReductionParameter;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReductionParameter, out softness);

            biasVelocity = MathHelper.Clamp(errorReductionParameter * error, -maxCorrectiveVelocity, maxCorrectiveVelocity);
            if (bounciness > F64.C0)
            {
                //Compute currently relative velocity for bounciness.
                FP relativeVelocity, dot;
                FPVector3.Dot(ref jLinearA, ref connectionA._linearVelocity, out relativeVelocity);
                FPVector3.Dot(ref jAngularA, ref connectionA._angularVelocity, out dot);
                relativeVelocity += dot;
                FPVector3.Dot(ref jLinearB, ref connectionB._linearVelocity, out dot);
                relativeVelocity += dot;
                FPVector3.Dot(ref jAngularB, ref connectionB._angularVelocity, out dot);
                relativeVelocity += dot;
                if (unadjustedError > F64.C0 && -relativeVelocity > bounceVelocityThreshold)
                    biasVelocity = MathHelper.Max(biasVelocity, ComputeBounceVelocity(-relativeVelocity));
                else if (unadjustedError < F64.C0 && relativeVelocity > bounceVelocityThreshold)
                    biasVelocity = MathHelper.Min(biasVelocity, -ComputeBounceVelocity(relativeVelocity));
            }


            //compute mass matrix
            FP entryA, entryB;
            FPVector3 intermediate;
            if (connectionA._isDynamic)
            {
                Matrix3x3.Transform(ref jAngularA, ref connectionA.inertiaTensorInverse, out intermediate);
                FPVector3.Dot(ref intermediate, ref jAngularA, out entryA);
                entryA += connectionA.inverseMass;
            }
            else
                entryA = F64.C0;
            if (connectionB._isDynamic)
            {
                Matrix3x3.Transform(ref jAngularB, ref connectionB.inertiaTensorInverse, out intermediate);
                FPVector3.Dot(ref intermediate, ref jAngularB, out entryB);
                entryB += connectionB.inverseMass;
            }
            else
                entryB = F64.C0;
            massMatrix = F64.C1 / (entryA + entryB + softness);


            
        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
        {
            //Warm starting
            FPVector3 impulse;
            if (connectionA._isDynamic)
            {
                FPVector3.Multiply(ref jLinearA, accumulatedImpulse, out impulse);
                connectionA.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularA, accumulatedImpulse, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Multiply(ref jLinearB, accumulatedImpulse, out impulse);
                connectionB.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularB, accumulatedImpulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        } 
    }
}