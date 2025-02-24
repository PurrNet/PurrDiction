using System;
using BEPUphysics.Entities;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Motors
{
    /// <summary>
    /// Constrains anchors on two entities to move relative to each other on a line.
    /// </summary>
    public class LinearAxisMotor : Motor, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private readonly MotorSettings1D settings;

        private FP accumulatedImpulse;
        private FP biasVelocity;
        private FPVector3 jAngularA, jAngularB;
        private FPVector3 jLinearA, jLinearB;
        private FPVector3 localAnchorA;
        private FPVector3 localAnchorB;
        private FP massMatrix;
        private FP error;
        private FPVector3 localAxis;
        private FPVector3 worldAxis;
        private FPVector3 rA; //Jacobian entry for entity A.
        private FPVector3 worldAnchorA;
        private FPVector3 worldAnchorB;
        private FPVector3 worldOffsetA, worldOffsetB;

        /// <summary>
        /// Constrains anchors on two entities to move relative to each other on a line.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the AnchorA, AnchorB and the Axis (or their entity-local versions).
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public LinearAxisMotor()
        {
            settings = new MotorSettings1D(this);
            IsActive = false;
        }


        /// <summary>
        /// Constrains anchors on two entities to move relative to each other on a line.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        /// <param name="anchorA">World space point to attach to connection A that will be constrained.</param>
        /// <param name="anchorB">World space point to attach to connection B that will be constrained.</param>
        /// <param name="axis">Limited axis in world space to attach to connection A.</param>
        public LinearAxisMotor(Entity connectionA, Entity connectionB, FPVector3 anchorA, FPVector3 anchorB, FPVector3 axis)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
            AnchorA = anchorA;
            AnchorB = anchorB;
            Axis = axis;

            settings = new MotorSettings1D(this);
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
        /// Gets or sets the motorized axis in world space.
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
        /// Gets or sets the offset from the first entity's center of mass to the anchor point in world space.
        /// </summary>
        public FPVector3 OffsetA
        {
            get { return worldOffsetA; }
            set
            {
                worldOffsetA = value;
                worldAnchorA = connectionA._position + worldOffsetA;
                Matrix3x3.TransformTranspose(ref worldOffsetA, ref connectionA._orientationMatrix, out localAnchorA); //Looks weird, but localAnchorA is "localOffsetA."

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
                Matrix3x3.TransformTranspose(ref worldOffsetB, ref connectionB._orientationMatrix, out localAnchorB);//Looks weird, but localAnchorB is "localOffsetB."
            }
        }

        /// <summary>
        /// Gets the motor's velocity and servo settings.
        /// </summary>
        public MotorSettings1D Settings
        {
            get { return settings; }
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
                FPVector3.Dot(ref jLinearA, ref connectionA._linearVelocity, out lambda);
                FPVector3.Dot(ref jAngularA, ref connectionA._angularVelocity, out dot);
                lambda += dot;
                FPVector3.Dot(ref jLinearB, ref connectionB._linearVelocity, out dot);
                lambda += dot;
                FPVector3.Dot(ref jAngularB, ref connectionB._angularVelocity, out dot);
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
        /// If the motor is in velocity only mode, the error will be zero.
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
            lambda = -lambda + biasVelocity - usedSoftness * accumulatedImpulse;

            //Transform to an impulse
            lambda *= massMatrix;

            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse + lambda, -maxForceDt, maxForceDt);
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

        public override void Update(FP dt)
        {
            //Compute the 'pre'-jacobians
            Matrix3x3.Transform(ref localAnchorA, ref connectionA._orientationMatrix, out worldOffsetA);
            Matrix3x3.Transform(ref localAnchorB, ref connectionB._orientationMatrix, out worldOffsetB);
            FPVector3.Add(ref worldOffsetA, ref connectionA._position, out worldAnchorA);
            FPVector3.Add(ref worldOffsetB, ref connectionB._position, out worldAnchorB);
            FPVector3.Subtract(ref worldAnchorB, ref connectionA._position, out rA);
            Matrix3x3.Transform(ref localAxis, ref connectionA._orientationMatrix, out worldAxis);

            FP updateRate = F64.C1 / dt;
            if (settings.mode == MotorMode.Servomechanism)
            {
                //Compute error
#if !WINDOWS
            FPVector3 separation = new FPVector3();
#else
                Vector3 separation;
#endif
                separation.x = worldAnchorB.x - worldAnchorA.x;
                separation.y = worldAnchorB.y - worldAnchorA.y;
                separation.z = worldAnchorB.z - worldAnchorA.z;

                FPVector3.Dot(ref separation, ref worldAxis, out error);

                //Compute error
                error = error - settings.servo.goal;


                //Compute bias
                FP absErrorOverDt = FP.Abs(error * updateRate);
                FP errorReduction;
                settings.servo.springSettings.ComputeErrorReductionAndSoftness(dt, updateRate, out errorReduction, out usedSoftness);
                biasVelocity = FP.Sign(error) * MathHelper.Min(settings.servo.baseCorrectiveSpeed, absErrorOverDt) + error * errorReduction;
                biasVelocity = MathHelper.Clamp(biasVelocity, -settings.servo.maxCorrectiveVelocity, settings.servo.maxCorrectiveVelocity);
            }
            else
            {
                biasVelocity = -settings.velocityMotor.goalVelocity;
                usedSoftness = settings.velocityMotor.softness * updateRate;
                error = F64.C0;
            }

            //Compute jacobians
            jLinearA = worldAxis;
            jLinearB.x = -jLinearA.x;
            jLinearB.y = -jLinearA.y;
            jLinearB.z = -jLinearA.z;
            FPVector3.Cross(ref rA, ref jLinearA, out jAngularA);
            FPVector3.Cross(ref worldOffsetB, ref jLinearB, out jAngularB);

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
            massMatrix = F64.C1 / (entryA + entryB + usedSoftness);

            //Update the maximum force
            ComputeMaxForces(settings.maximumForce, dt);



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