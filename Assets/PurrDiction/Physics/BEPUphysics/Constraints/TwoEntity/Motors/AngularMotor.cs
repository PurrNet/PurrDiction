using System;
using BEPUphysics.Entities;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Motors
{
    /// <summary>
    /// Constraint which attempts to restrict the relative angular motion of two entities.
    /// Can use a target relative orientation to apply additional force.
    /// </summary>
    public class AngularMotor : Motor, I3DImpulseConstraintWithError, I3DJacobianConstraint
    {
        private readonly JointBasis3D basis = new JointBasis3D();

        private readonly MotorSettingsOrientation settings;
        private FPVector3 accumulatedImpulse;


        private FP angle;
        private FPVector3 axis;
        private FPVector3 biasVelocity;
        private Matrix3x3 effectiveMassMatrix;

        /// <summary>
        /// Constructs a new constraint which attempts to restrict the relative angular motion of two entities.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB).
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public AngularMotor()
        {
            IsActive = false;
            settings = new MotorSettingsOrientation(this);
        }

        /// <summary>
        /// Constructs a new constraint which attempts to restrict the relative angular motion of two entities.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        public AngularMotor(Entity connectionA, Entity connectionB)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            settings = new MotorSettingsOrientation(this);

            //Compute the rotation from A to B in A's local space.
            FPQuaternion orientationAConjugate;
            FPQuaternion.Conjugate(ref connectionA.orientation, out orientationAConjugate);
            FPQuaternion.Concatenate(ref connectionB.orientation, ref orientationAConjugate, out settings.servo.goal);

        }

        /// <summary>
        /// Gets the basis attached to entity A.
        /// The target velocity/orientation of this motor is transformed by the basis.
        /// </summary>
        public JointBasis3D Basis
        {
            get { return basis; }
        }

        /// <summary>
        /// Gets the motor's velocity and servo settings.
        /// </summary>
        public MotorSettingsOrientation Settings
        {
            get { return settings; }
        }

        #region I3DImpulseConstraintWithError Members

        /// <summary>
        /// Gets the current relative velocity between the connected entities with respect to the constraint.
        /// </summary>
        public FPVector3 RelativeVelocity
        {
            get { return connectionA.angularVelocity - connectionB.angularVelocity; }
        }

        /// <summary>
        /// Gets the total impulse applied by this constraint.
        /// </summary>
        public FPVector3 TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the current constraint error.
        /// If the motor is in velocity only mode, error is zero.
        /// </summary>
        public FPVector3 Error
        {
            get { return axis * angle; }
        }

        #endregion

        #region I3DJacobianConstraint Members

        /// <summary>
        /// Gets the linear jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobianX">First linear jacobian entry for the first connected entity.</param>
        /// <param name="jacobianY">Second linear jacobian entry for the first connected entity.</param>
        /// <param name="jacobianZ">Third linear jacobian entry for the first connected entity.</param>
        public void GetLinearJacobianA(out FPVector3 jacobianX, out FPVector3 jacobianY, out FPVector3 jacobianZ)
        {
            jacobianX = Toolbox.ZeroVector;
            jacobianY = Toolbox.ZeroVector;
            jacobianZ = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the linear jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobianX">First linear jacobian entry for the second connected entity.</param>
        /// <param name="jacobianY">Second linear jacobian entry for the second connected entity.</param>
        /// <param name="jacobianZ">Third linear jacobian entry for the second connected entity.</param>
        public void GetLinearJacobianB(out FPVector3 jacobianX, out FPVector3 jacobianY, out FPVector3 jacobianZ)
        {
            jacobianX = Toolbox.ZeroVector;
            jacobianY = Toolbox.ZeroVector;
            jacobianZ = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobianX">First angular jacobian entry for the first connected entity.</param>
        /// <param name="jacobianY">Second angular jacobian entry for the first connected entity.</param>
        /// <param name="jacobianZ">Third angular jacobian entry for the first connected entity.</param>
        public void GetAngularJacobianA(out FPVector3 jacobianX, out FPVector3 jacobianY, out FPVector3 jacobianZ)
        {
            jacobianX = Toolbox.RightVector;
            jacobianY = Toolbox.UpVector;
            jacobianZ = Toolbox.BackVector;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobianX">First angular jacobian entry for the second connected entity.</param>
        /// <param name="jacobianY">Second angular jacobian entry for the second connected entity.</param>
        /// <param name="jacobianZ">Third angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobianX, out FPVector3 jacobianY, out FPVector3 jacobianZ)
        {
            jacobianX = Toolbox.RightVector;
            jacobianY = Toolbox.UpVector;
            jacobianZ = Toolbox.BackVector;
        }

        /// <summary>
        /// Gets the mass matrix of the constraint.
        /// </summary>
        /// <param name="outputMassMatrix">Constraint's mass matrix.</param>
        public void GetMassMatrix(out Matrix3x3 outputMassMatrix)
        {
            outputMassMatrix = effectiveMassMatrix;
        }

        #endregion

        /// <summary>
        /// Applies the corrective impulses required by the constraint.
        /// </summary>
        public override FP SolveIteration()
        {
#if !WINDOWS
            FPVector3 lambda = new FPVector3();
#else
            Vector3 lambda;
#endif
            FPVector3 aVel = connectionA.angularVelocity;
            FPVector3 bVel = connectionB.angularVelocity;
            lambda.x = bVel.x - aVel.x - biasVelocity.x - usedSoftness * accumulatedImpulse.x;
            lambda.y = bVel.y - aVel.y - biasVelocity.y - usedSoftness * accumulatedImpulse.y;
            lambda.z = bVel.z - aVel.z - biasVelocity.z - usedSoftness * accumulatedImpulse.z;

            Matrix3x3.Transform(ref lambda, ref effectiveMassMatrix, out lambda);

            FPVector3 previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse.x += lambda.x;
            accumulatedImpulse.y += lambda.y;
            accumulatedImpulse.z += lambda.z;
            FP sumLengthSquared = accumulatedImpulse.LengthSquared();

            if (sumLengthSquared > maxForceDtSquared)
            {
                //max / impulse gives some value 0 < x < 1.  Basically, normalize the vector (divide by the length) and scale by the maximum.
                FP multiplier = maxForceDt / FP.Sqrt(sumLengthSquared);
                accumulatedImpulse.x *= multiplier;
                accumulatedImpulse.y *= multiplier;
                accumulatedImpulse.z *= multiplier;

                //Since the limit was exceeded by this corrective impulse, limit it so that the accumulated impulse remains constrained.
                lambda.x = accumulatedImpulse.x - previousAccumulatedImpulse.x;
                lambda.y = accumulatedImpulse.y - previousAccumulatedImpulse.y;
                lambda.z = accumulatedImpulse.z - previousAccumulatedImpulse.z;
            }


            if (connectionA.isDynamic)
            {
                connectionA.ApplyAngularImpulse(ref lambda);
            }
            if (connectionB.isDynamic)
            {
                FPVector3 torqueB;
                FPVector3.Negate(ref lambda, out torqueB);
                connectionB.ApplyAngularImpulse(ref torqueB);
            }

            return (FP.Abs(lambda.x) + FP.Abs(lambda.y) + FP.Abs(lambda.z));
        }

        /// <summary>
        /// Initializes the constraint for the current frame.
        /// </summary>
        /// <param name="dt">Time between frames.</param>
        public override void Update(FP dt)
        {
            basis.rotationMatrix = connectionA.orientationMatrix;
            basis.ComputeWorldSpaceAxes();

            FP inverseDt = F64.C1 / dt;
            if (settings.mode == MotorMode.Servomechanism) //Only need to do the bulk of this work if it's a servo.
            {

                //The error is computed using this equation:
                //GoalRelativeOrientation * ConnectionA.Orientation * Error = ConnectionB.Orientation
                //GoalRelativeOrientation is the original rotation from A to B in A's local space.
                //Multiplying by A's orientation gives us where B *should* be.
                //Of course, B won't be exactly where it should be after initialization.
                //The Error component holds the difference between what is and what should be.
                //Error = (GoalRelativeOrientation * ConnectionA.Orientation)^-1 * ConnectionB.Orientation

                //ConnectionA.Orientation is replaced in the above by the world space basis orientation.
                FPQuaternion worldBasis = FPQuaternion.CreateFromRotationMatrix(basis.WorldTransform);

                FPQuaternion bTarget;
                FPQuaternion.Concatenate(ref settings.servo.goal, ref worldBasis, out bTarget);
                FPQuaternion bTargetConjugate;
                FPQuaternion.Conjugate(ref bTarget, out bTargetConjugate);

                FPQuaternion error;
                FPQuaternion.Concatenate(ref bTargetConjugate, ref connectionB.orientation, out error);


                FP errorReduction;
                settings.servo.springSettings.ComputeErrorReductionAndSoftness(dt, inverseDt, out errorReduction, out usedSoftness);

                //Turn this into an axis-angle representation.
                FPQuaternion.GetAxisAngleFromQuaternion(ref error, out axis, out angle);

                //Scale the axis by the desired velocity if the angle is sufficiently large (epsilon).
                if (angle > Toolbox.BigEpsilon)
                {
                    FP velocity = -(MathHelper.Min(settings.servo.baseCorrectiveSpeed, angle * inverseDt) + angle * errorReduction);

                    biasVelocity.x = axis.x * velocity;
                    biasVelocity.y = axis.y * velocity;
                    biasVelocity.z = axis.z * velocity;


                    //Ensure that the corrective velocity doesn't exceed the max.
                    FP length = biasVelocity.LengthSquared();
                    if (length > settings.servo.maxCorrectiveVelocitySquared)
                    {
                        FP multiplier = settings.servo.maxCorrectiveVelocity / FP.Sqrt(length);
                        biasVelocity.x *= multiplier;
                        biasVelocity.y *= multiplier;
                        biasVelocity.z *= multiplier;
                    }
                }
                else
                {
                    biasVelocity.x = F64.C0;
                    biasVelocity.y = F64.C0;
                    biasVelocity.z = F64.C0;
                }
            }
            else
            {
                usedSoftness = settings.velocityMotor.softness * inverseDt;
                angle = F64.C0; //Zero out the error;
                Matrix3x3 transform = basis.WorldTransform;
                Matrix3x3.Transform(ref settings.velocityMotor.goalVelocity, ref transform, out biasVelocity);
            }

            //Compute effective mass
            Matrix3x3.Add(ref connectionA.inertiaTensorInverse, ref connectionB.inertiaTensorInverse, out effectiveMassMatrix);
            effectiveMassMatrix.M11 += usedSoftness;
            effectiveMassMatrix.M22 += usedSoftness;
            effectiveMassMatrix.M33 += usedSoftness;
            Matrix3x3.Invert(ref effectiveMassMatrix, out effectiveMassMatrix);

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
            //Apply accumulated impulse
            if (connectionA.isDynamic)
            {
                connectionA.ApplyAngularImpulse(ref accumulatedImpulse);
            }
            if (connectionB.isDynamic)
            {
                FPVector3 torqueB;
                FPVector3.Negate(ref accumulatedImpulse, out torqueB);
                connectionB.ApplyAngularImpulse(ref torqueB);
            }
        }
    }
}