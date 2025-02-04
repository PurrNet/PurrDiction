using System;
using BEPUphysics.Entities;
 
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Motors
{
    /// <summary>
    /// Tries to rotate two entities so that they reach a specified relative orientation or speed around an axis.
    /// </summary>
    public class RevoluteMotor : Motor, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private readonly JointBasis2D basis = new JointBasis2D();
        private readonly MotorSettings1D settings;
        private FP accumulatedImpulse;
        protected FP biasVelocity;
        private FPVector3 jacobianA, jacobianB;
        private FP error;

        private FPVector3 localTestAxis;


        private FPVector3 worldTestAxis;
        private FP velocityToImpulse;

        /// <summary>
        /// Constructs a new constraint tries to rotate two entities so that they reach a specified relative orientation around an axis.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the Basis and TestAxis.
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public RevoluteMotor()
        {
            settings = new MotorSettings1D(this);
            IsActive = false;
        }

        /// <summary>
        /// Constructs a new constraint tries to rotate two entities so that they reach a specified relative orientation around an axis.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        /// <param name="motorizedAxis">Rotation axis to control in world space.</param>
        public RevoluteMotor(Entity connectionA, Entity connectionB, FPVector3 motorizedAxis)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
            SetupJointTransforms(motorizedAxis);

            settings = new MotorSettings1D(this);
        }

        /// <summary>
        /// Gets the basis attached to entity A.
        /// The primary axis represents the motorized axis of rotation.  The 'measurement plane' which the test axis is tested against is based on this primary axis.
        /// The x axis defines the 'base' direction on the measurement plane corresponding to 0 degrees of relative rotation.
        /// </summary>
        public JointBasis2D Basis
        {
            get { return basis; }
        }

        /// <summary>
        /// Gets or sets the axis attached to entity B in its local space.
        /// This axis is projected onto the x and y axes of transformA to determine the hinge angle.
        /// </summary>
        public FPVector3 LocalTestAxis
        {
            get { return localTestAxis; }
            set
            {
                localTestAxis = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localTestAxis, ref connectionB.orientationMatrix, out worldTestAxis);
            }
        }

        /// <summary>
        /// Gets the motor's velocity and servo settings.
        /// </summary>
        public MotorSettings1D Settings
        {
            get { return settings; }
        }

        /// <summary>
        /// Gets or sets the axis attached to entity B in world space.
        /// This axis is projected onto the x and y axes of the Basis attached to entity A to determine the hinge angle.
        /// </summary>
        public FPVector3 TestAxis
        {
            get { return worldTestAxis; }
            set
            {
                worldTestAxis = FPVector3.Normalize(value);
                Matrix3x3.TransformTranspose(ref worldTestAxis, ref connectionB.orientationMatrix, out localTestAxis);
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
                FP velocityA, velocityB;
                FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianA, out velocityA);
                FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianB, out velocityB);
                return velocityA + velocityB;
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
        /// If the motor is in velocity only mode, the error is zero.
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
            jacobian = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the linear jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Linear jacobian entry for the second connected entity.</param>
        public void GetLinearJacobianB(out FPVector3 jacobian)
        {
            jacobian = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the first connected entity.</param>
        public void GetAngularJacobianA(out FPVector3 jacobian)
        {
            jacobian = jacobianA;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobian">Angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobian)
        {
            jacobian = jacobianB;
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
        /// Sets up the joint transforms by automatically creating perpendicular vectors to complete the bases.
        /// </summary>
        /// <param name="motorizedAxis">Axis around which the motor acts.</param>
        public void SetupJointTransforms(FPVector3 motorizedAxis)
        {
            //Compute a vector which is perpendicular to the axis.  It'll be added in local space to both connections.
            FPVector3 xAxis;
            FPVector3.Cross(ref motorizedAxis, ref Toolbox.UpVector, out xAxis);
            FP length = xAxis.LengthSquared();
            if (length < Toolbox.Epsilon)
            {
                FPVector3.Cross(ref motorizedAxis, ref Toolbox.RightVector, out xAxis);
            }

            //Put the axes into the joint transform of A.
            basis.rotationMatrix = connectionA.orientationMatrix;
            basis.SetWorldAxes(motorizedAxis, xAxis);


            //Put the axes into the 'joint transform' of B too.
            TestAxis = basis.xAxis;
        }

        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {
            //Transform the axes into world space.
            basis.rotationMatrix = connectionA.orientationMatrix;
            basis.ComputeWorldSpaceAxes();
            Matrix3x3.Transform(ref localTestAxis, ref connectionB.orientationMatrix, out worldTestAxis);

            FP updateRate = F64.C1 / dt;
            if (settings.mode == MotorMode.Servomechanism)
            {
                FP y, x;
                FPVector3 yAxis;
                FPVector3.Cross(ref basis.primaryAxis, ref basis.xAxis, out yAxis);
                FPVector3.Dot(ref worldTestAxis, ref yAxis, out y);
                FPVector3.Dot(ref worldTestAxis, ref basis.xAxis, out x);
                var angle = FP.FastAtan2(y, x);

                //****** VELOCITY BIAS ******//
                //Compute the correction velocity.
                error = GetDistanceFromGoal(angle);


                FP absErrorOverDt = FP.Abs(error * updateRate);
                FP errorReduction;
                settings.servo.springSettings.ComputeErrorReductionAndSoftness(dt, updateRate, out errorReduction, out usedSoftness);
                biasVelocity = FP.Sign(error) * MathHelper.Min(settings.servo.baseCorrectiveSpeed, absErrorOverDt) + error * errorReduction;

                biasVelocity = MathHelper.Clamp(biasVelocity, -settings.servo.maxCorrectiveVelocity, settings.servo.maxCorrectiveVelocity);
            }
            else
            {
                biasVelocity = settings.velocityMotor.goalVelocity;
                usedSoftness = settings.velocityMotor.softness * updateRate;
                error = F64.C0;
            }


            //Compute the jacobians
            jacobianA = basis.primaryAxis;
            jacobianB.x = -jacobianA.x;
            jacobianB.y = -jacobianA.y;
            jacobianB.z = -jacobianA.z;


            //****** EFFECTIVE MASS MATRIX ******//
            //Connection A's contribution to the mass matrix
            FP entryA;
            FPVector3 transformedAxis;
            if (connectionA.isDynamic)
            {
                Matrix3x3.Transform(ref jacobianA, ref connectionA.inertiaTensorInverse, out transformedAxis);
                FPVector3.Dot(ref transformedAxis, ref jacobianA, out entryA);
            }
            else
                entryA = F64.C0;

            //Connection B's contribution to the mass matrix
            FP entryB;
            if (connectionB.isDynamic)
            {
                Matrix3x3.Transform(ref jacobianB, ref connectionB.inertiaTensorInverse, out transformedAxis);
                FPVector3.Dot(ref transformedAxis, ref jacobianB, out entryB);
            }
            else
                entryB = F64.C0;

            //Compute the inverse mass matrix
            velocityToImpulse = F64.C1 / (usedSoftness + entryA + entryB);


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
            //****** WARM STARTING ******//
            //Apply accumulated impulse
            FPVector3 impulse;
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref jacobianA, accumulatedImpulse, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Multiply(ref jacobianB, accumulatedImpulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        }

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            FP velocityA, velocityB;
            //Find the velocity contribution from each connection
            FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianA, out velocityA);
            FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianB, out velocityB);
            //Add in the constraint space bias velocity
            FP lambda = -(velocityA + velocityB) - biasVelocity - usedSoftness * accumulatedImpulse;

            //Transform to an impulse
            lambda *= velocityToImpulse;

            //Accumulate the impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse + lambda, -maxForceDt, maxForceDt);
            lambda = accumulatedImpulse - previousAccumulatedImpulse;

            //Apply the impulse
            FPVector3 impulse;
            if (connectionA.isDynamic)
            {
                FPVector3.Multiply(ref jacobianA, lambda, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB.isDynamic)
            {
                FPVector3.Multiply(ref jacobianB, lambda, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }

            return FP.Abs(lambda);
        }



        private FP GetDistanceFromGoal(FP angle)
        {

            FP forwardDistance;
            FP goalAngle = MathHelper.WrapAngle(settings.servo.goal);
            if (goalAngle > F64.C0)
            {
                if (angle > goalAngle)
                    forwardDistance = angle - goalAngle;
                else if (angle > F64.C0)
                    forwardDistance = MathHelper.TwoPi - goalAngle + angle;
                else //if (angle <= 0)
                    forwardDistance = MathHelper.TwoPi - goalAngle + angle;
            }
            else
            {
                if (angle < goalAngle)
                    forwardDistance = MathHelper.TwoPi - goalAngle + angle;
                else //if (angle < 0)
                    forwardDistance = angle - goalAngle;
                //else //if (currentAngle >= 0)
                //    return angle - myMinimumAngle;
            }
            return forwardDistance > MathHelper.Pi ? MathHelper.TwoPi - forwardDistance : -forwardDistance;
        }
    }
}