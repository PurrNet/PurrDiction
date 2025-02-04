using System;
using BEPUphysics.Entities;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.JointLimits
{
    /// <summary>
    /// A modified distance constraint allowing a range of lengths between two anchor points.
    /// </summary>
    public class DistanceLimit : JointLimit, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private FP accumulatedImpulse;
        private FPVector3 anchorA;

        private FPVector3 anchorB;
        private FP biasVelocity;
        private FPVector3 jAngularA, jAngularB;
        private FPVector3 jLinearA, jLinearB;
        private FP error;

        private FPVector3 localAnchorA;

        private FPVector3 localAnchorB;

        /// <summary>
        /// Maximum distance allowed between the anchors.
        /// </summary>
        protected FP maximumLength;

        /// <summary>
        /// Minimum distance maintained between the anchors.
        /// </summary>
        protected FP minimumLength;


        private FPVector3 offsetA, offsetB;
        private FP velocityToImpulse;

        /// <summary>
        /// Constructs a distance limit joint.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the WorldAnchorA and WorldAnchorB (or their entity-local versions)
        /// and the MinimumLength and MaximumLength.
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public DistanceLimit()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a distance limit joint.
        /// </summary>
        /// <param name="connectionA">First body connected to the distance limit.</param>
        /// <param name="connectionB">Second body connected to the distance limit.</param>
        /// <param name="anchorA">Connection to the spring from the first connected body in world space.</param>
        /// <param name="anchorB"> Connection to the spring from the second connected body in world space.</param>
        /// <param name="minimumLength">Minimum distance maintained between the anchors.</param>
        /// <param name="maximumLength">Maximum distance allowed between the anchors.</param>
        public DistanceLimit(Entity connectionA, Entity connectionB, FPVector3 anchorA, FPVector3 anchorB, FP minimumLength, FP maximumLength)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
            MinimumLength = minimumLength;
            MaximumLength = maximumLength;

            WorldAnchorA = anchorA;
            WorldAnchorB = anchorB;
        }

        /// <summary>
        /// Gets or sets the first entity's connection point in local space.
        /// </summary>
        public FPVector3 LocalAnchorA
        {
            get { return localAnchorA; }
            set
            {
                localAnchorA = value;
                Matrix3x3.Transform(ref localAnchorA, ref connectionA.orientationMatrix, out anchorA);
                anchorA += connectionA.position;
            }
        }

        /// <summary>
        /// Gets or sets the first entity's connection point in local space.
        /// </summary>
        public FPVector3 LocalAnchorB
        {
            get { return localAnchorB; }
            set
            {
                localAnchorB = value;
                Matrix3x3.Transform(ref localAnchorB, ref connectionB.orientationMatrix, out anchorB);
                anchorB += connectionB.position;
            }
        }

        /// <summary>
        /// Gets or sets the maximum distance allowed between the anchors.
        /// </summary>
        public FP MaximumLength
        {
            get { return maximumLength; }
            set
            {
                maximumLength = MathHelper.Max(F64.C0, value);
                minimumLength = MathHelper.Min(minimumLength, maximumLength);
            }
        }

        /// <summary>
        /// Gets or sets the minimum distance maintained between the anchors.
        /// </summary>
        public FP MinimumLength
        {
            get { return minimumLength; }
            set
            {
                minimumLength = MathHelper.Max(F64.C0, value);
                maximumLength = MathHelper.Max(minimumLength, maximumLength);
            }
        }

        /// <summary>
        /// Gets or sets the connection to the distance constraint from the first connected body in world space.
        /// </summary>
        public FPVector3 WorldAnchorA
        {
            get { return anchorA; }
            set
            {
                anchorA = value;
                localAnchorA = FPQuaternion.Transform(anchorA - connectionA.position, FPQuaternion.Conjugate(connectionA.orientation));
            }
        }

        /// <summary>
        /// Gets or sets the connection to the distance constraint from the second connected body in world space.
        /// </summary>
        public FPVector3 WorldAnchorB
        {
            get { return anchorB; }
            set
            {
                anchorB = value;
                localAnchorB = FPQuaternion.Transform(anchorB - connectionB.position, FPQuaternion.Conjugate(connectionB.orientation));
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
                    FPVector3.Dot(ref jLinearA, ref connectionA.linearVelocity, out lambda);
                    FPVector3.Dot(ref jAngularA, ref connectionA.angularVelocity, out dot);
                    lambda += dot;
                    FPVector3.Dot(ref jLinearB, ref connectionB.linearVelocity, out dot);
                    lambda += dot;
                    FPVector3.Dot(ref jAngularB, ref connectionB.angularVelocity, out dot);
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
            outputMassMatrix = velocityToImpulse;
        }

        #endregion

        /// <summary>
        /// Calculates and applies corrective impulses.
        /// Called automatically by space.
        /// </summary>
        public override FP SolveIteration()
        {
            //Compute the current relative velocity.
            FP lambda, dot;
            FPVector3.Dot(ref jLinearA, ref connectionA.linearVelocity, out lambda);
            FPVector3.Dot(ref jAngularA, ref connectionA.angularVelocity, out dot);
            lambda += dot;
            FPVector3.Dot(ref jLinearB, ref connectionB.linearVelocity, out dot);
            lambda += dot;
            FPVector3.Dot(ref jAngularB, ref connectionB.angularVelocity, out dot);
            lambda += dot;

            //Add in the constraint space bias velocity
            lambda = -lambda + biasVelocity - softness * accumulatedImpulse;

            //Transform to an impulse
            lambda *= velocityToImpulse;

            //Clamp accumulated impulse (can't go negative)
            FP previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse = MathHelper.Max(accumulatedImpulse + lambda, F64.C0);
            lambda = accumulatedImpulse - previousAccumulatedImpulse;

            //Apply the impulse
            FPVector3 impulse;
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref jLinearA, lambda, out impulse);
                connectionA.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularA, lambda, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Multiply(ref jLinearB, lambda, out impulse);
                connectionB.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularB, lambda, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }

            return (FP.Abs(lambda));
        }

        /// <summary>
        /// Calculates necessary information for velocity solving.
        /// </summary>
        /// <param name="dt">Time in seconds since the last update.</param>
        public override void Update(FP dt)
        {
            //Transform the anchors and offsets into world space.
            Matrix3x3.Transform(ref localAnchorA, ref connectionA.orientationMatrix, out offsetA);
            Matrix3x3.Transform(ref localAnchorB, ref connectionB.orientationMatrix, out offsetB);
            FPVector3.Add(ref connectionA.position, ref offsetA, out anchorA);
            FPVector3.Add(ref connectionB.position, ref offsetB, out anchorB);

            //Compute the distance.
            FPVector3 separation;
            FPVector3.Subtract(ref anchorB, ref anchorA, out separation);
            FP distance = separation.Length();
            if (distance <= maximumLength && distance >= minimumLength)
            {
                isActiveInSolver = false;
                accumulatedImpulse = F64.C0;
                error = F64.C0;
                isLimitActive = false;
                return;
            }
            isLimitActive = true;


            //Compute jacobians
            if (distance > maximumLength)
            {
                //If it's beyond the max, all of the jacobians are reversed compared to what they are when it's below the min.
                if (distance > Toolbox.Epsilon)
                {
                    jLinearA.x = separation.x / distance;
                    jLinearA.y = separation.y / distance;
                    jLinearA.z = separation.z / distance;
                }
                else
                    jLinearB = Toolbox.ZeroVector;

                jLinearB.x = -jLinearA.x;
                jLinearB.y = -jLinearA.y;
                jLinearB.z = -jLinearA.z;

                FPVector3.Cross(ref jLinearA, ref offsetA, out jAngularA);
                //Still need to negate angular A.  It's done after the effective mass matrix.
                FPVector3.Cross(ref jLinearA, ref offsetB, out jAngularB);
            }
            else
            {
                if (distance > Toolbox.Epsilon)
                {
                    jLinearB.x = separation.x / distance;
                    jLinearB.y = separation.y / distance;
                    jLinearB.z = separation.z / distance;
                }
                else
                    jLinearB = Toolbox.ZeroVector;

                jLinearA.x = -jLinearB.x;
                jLinearA.y = -jLinearB.y;
                jLinearA.z = -jLinearB.z;

                FPVector3.Cross(ref offsetA, ref jLinearB, out jAngularA);
                //Still need to negate angular A.  It's done after the effective mass matrix.
                FPVector3.Cross(ref offsetB, ref jLinearB, out jAngularB);
            }


            //Debug.WriteLine("BiasVelocity: " + biasVelocity);


            //Compute effective mass matrix
            if (connectionA.isDynamic && connectionB.isDynamic)
            {
                FPVector3 aAngular;
                Matrix3x3.Transform(ref jAngularA, ref connectionA.localInertiaTensorInverse, out aAngular);
                FPVector3.Cross(ref aAngular, ref offsetA, out aAngular);
                FPVector3 bAngular;
                Matrix3x3.Transform(ref jAngularB, ref connectionB.localInertiaTensorInverse, out bAngular);
                FPVector3.Cross(ref bAngular, ref offsetB, out bAngular);
                FPVector3.Add(ref aAngular, ref bAngular, out aAngular);
                FPVector3.Dot(ref aAngular, ref jLinearB, out velocityToImpulse);
                velocityToImpulse += connectionA.inverseMass + connectionB.inverseMass;
            }
            else if (connectionA.isDynamic)
            {
                FPVector3 aAngular;
                Matrix3x3.Transform(ref jAngularA, ref connectionA.localInertiaTensorInverse, out aAngular);
                FPVector3.Cross(ref aAngular, ref offsetA, out aAngular);
                FPVector3.Dot(ref aAngular, ref jLinearB, out velocityToImpulse);
                velocityToImpulse += connectionA.inverseMass;
            }
            else if (connectionB.isDynamic)
            {
                FPVector3 bAngular;
                Matrix3x3.Transform(ref jAngularB, ref connectionB.localInertiaTensorInverse, out bAngular);
                FPVector3.Cross(ref bAngular, ref offsetB, out bAngular);
                FPVector3.Dot(ref bAngular, ref jLinearB, out velocityToImpulse);
                velocityToImpulse += connectionB.inverseMass;
            }
            else
            {
                //No point in trying to solve with two kinematics.
                isActiveInSolver = false;
                accumulatedImpulse = F64.C0;
                return;
            }

            FP errorReduction;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReduction, out softness);

            velocityToImpulse = F64.C1 / (softness + velocityToImpulse);
            //Finish computing jacobian; it's down here as an optimization (since it didn't need to be negated in mass matrix)
            jAngularA.x = -jAngularA.x;
            jAngularA.y = -jAngularA.y;
            jAngularA.z = -jAngularA.z;

            //Compute bias velocity
            if (distance > maximumLength)
                error = MathHelper.Max(F64.C0, distance - maximumLength - Margin);
            else
                error = MathHelper.Max(F64.C0, minimumLength - Margin - distance);
            biasVelocity = MathHelper.Min(errorReduction * error, maxCorrectiveVelocity);
            if (bounciness > F64.C0)
            {
                //Compute currently relative velocity for bounciness.
                FP relativeVelocity, dot;
                FPVector3.Dot(ref jLinearA, ref connectionA.linearVelocity, out relativeVelocity);
                FPVector3.Dot(ref jAngularA, ref connectionA.angularVelocity, out dot);
                relativeVelocity += dot;
                FPVector3.Dot(ref jLinearB, ref connectionB.linearVelocity, out dot);
                relativeVelocity += dot;
                FPVector3.Dot(ref jAngularB, ref connectionB.angularVelocity, out dot);
                relativeVelocity += dot;
                biasVelocity = MathHelper.Max(biasVelocity, ComputeBounceVelocity(-relativeVelocity));
            }


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
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref jLinearA, accumulatedImpulse, out impulse);
                connectionA.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularA, accumulatedImpulse, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Multiply(ref jLinearB, accumulatedImpulse, out impulse);
                connectionB.ApplyLinearImpulse(ref impulse);
                FPVector3.Multiply(ref jAngularB, accumulatedImpulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        }
    }
}