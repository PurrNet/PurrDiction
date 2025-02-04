using System;
using BEPUphysics.Entities;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Joints
{
    /// <summary>
    /// Constraint which tries to maintain the distance between points on two entities.
    /// </summary>
    public class DistanceJoint : Joint, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private FP accumulatedImpulse;
        private FPVector3 anchorA;

        private FPVector3 anchorB;
        private FP biasVelocity;
        private FPVector3 jAngularA, jAngularB;
        private FPVector3 jLinearA, jLinearB;

        /// <summary>
        /// Distance maintained between the anchors.
        /// </summary>
        protected FP distance;

        private FP error;

        private FPVector3 localAnchorA;

        private FPVector3 localAnchorB;


        private FPVector3 offsetA, offsetB;
        private FP velocityToImpulse;

        /// <summary>
        /// Constructs a distance joint.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the anchors (WorldAnchorA, WorldAnchorB or LocalAnchorA, LocalAnchorB)
        /// and the desired Distance.
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public DistanceJoint()
        {
            IsActive = false;
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            Distance = (anchorA - anchorB).Length();

            WorldAnchorA = anchorA;
            WorldAnchorB = anchorB;
        }

        /// <summary>
        /// Constructs a distance joint.
        /// </summary>
        /// <param name="connectionA">First body connected to the distance joint.</param>
        /// <param name="connectionB">Second body connected to the distance joint.</param>
        /// <param name="anchorA">Connection to the distance joint from the first connected body in world space.</param>
        /// <param name="anchorB"> Connection to the distance joint from the second connected body in world space.</param>
        public DistanceJoint(Entity connectionA, Entity connectionB, FPVector3 anchorA, FPVector3 anchorB)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            Distance = (anchorA - anchorB).Length();

            WorldAnchorA = anchorA;
            WorldAnchorB = anchorB;
        }

        /// <summary>
        /// Gets or sets the distance maintained between the anchors.
        /// </summary>
        public FP Distance
        {
            get { return distance; }
            set { distance = MathHelper.Max(F64.C0, value); }
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

            //Accumulate impulse
            accumulatedImpulse += lambda;

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
            FP currentDistance = separation.Length();

            //Compute jacobians
            if (currentDistance > Toolbox.Epsilon)
            {
                jLinearB.x = separation.x / currentDistance;
                jLinearB.y = separation.y / currentDistance;
                jLinearB.z = separation.z / currentDistance;
            }
            else
                jLinearB = Toolbox.ZeroVector;

            jLinearA.x = -jLinearB.x;
            jLinearA.y = -jLinearB.y;
            jLinearA.z = -jLinearB.z;

            FPVector3.Cross(ref offsetA, ref jLinearB, out jAngularA);
            //Still need to negate angular A.  It's done after the effective mass matrix.
            FPVector3.Cross(ref offsetB, ref jLinearB, out jAngularB);


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
            error = distance - currentDistance;
            biasVelocity = MathHelper.Clamp(error * errorReduction, -maxCorrectiveVelocity, maxCorrectiveVelocity);



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