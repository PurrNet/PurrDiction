using System;
using BEPUphysics.Constraints.TwoEntity.Motors;
using BEPUphysics.Entities;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.SingleEntity
{
    /// <summary>
    /// Constraint which tries to push an entity to a desired location.
    /// </summary>
    public class SingleEntityLinearMotor : SingleEntityConstraint, I3DImpulseConstraintWithError
    {
        private readonly MotorSettings3D settings;

        /// <summary>
        /// Sum of forces applied to the constraint in the past.
        /// </summary>
        private FPVector3 accumulatedImpulse = FPVector3.zero;

        private FPVector3 biasVelocity;
        private Matrix3x3 effectiveMassMatrix;

        /// <summary>
        /// Maximum impulse that can be applied in a single frame.
        /// </summary>
        private FP maxForceDt;

        /// <summary>
        /// Maximum impulse that can be applied in a single frame, squared.
        /// This is computed in the prestep to avoid doing extra multiplies in the more-often called applyImpulse method.
        /// </summary>
        private FP maxForceDtSquared;

        private FPVector3 error;

        private FPVector3 localPoint;

        private FPVector3 worldPoint;

        private FPVector3 r;
        private FP usedSoftness;

        /// <summary>
        /// Gets or sets the entity affected by the constraint.
        /// </summary>
        public override Entity Entity
        {
            get
            {
                return base.Entity;
            }
            set
            {
                if (Entity != value)
                    accumulatedImpulse = new FPVector3();
                base.Entity = value;
            }
        }


        /// <summary>
        /// Constructs a new single body linear motor.  This motor will try to move a single entity to a goal velocity or to a goal position.
        /// </summary>
        /// <param name="entity">Entity to affect.</param>
        /// <param name="point">Point in world space attached to the entity that will be motorized.</param>
        public SingleEntityLinearMotor(Entity entity, FPVector3 point)
        {
            Entity = entity;
            Point = point;

            settings = new MotorSettings3D(this) {servo = {goal = point}};
            //Not really necessary, just helps prevent 'snapping'.
        }


        /// <summary>
        /// Constructs a new single body linear motor.  This motor will try to move a single entity to a goal velocity or to a goal position.
        /// This constructor will start the motor with isActive = false.
        /// </summary>
        public SingleEntityLinearMotor()
        {
            settings = new MotorSettings3D(this);
            IsActive = false;
        }

        /// <summary>
        /// Point attached to the entity in its local space that is motorized.
        /// </summary>
        public FPVector3 LocalPoint
        {
            get { return localPoint; }
            set
            {
                localPoint = value;
                Matrix3x3.Transform(ref localPoint, ref entity.orientationMatrix, out worldPoint);
                FPVector3.Add(ref worldPoint, ref entity.position, out worldPoint);
            }
        }

        /// <summary>
        /// Point attached to the entity in world space that is motorized.
        /// </summary>
        public FPVector3 Point
        {
            get { return worldPoint; }
            set
            {
                worldPoint = value;
                FPVector3.Subtract(ref worldPoint, ref entity.position, out localPoint);
                Matrix3x3.TransformTranspose(ref localPoint, ref entity.orientationMatrix, out localPoint);
            }
        }

        /// <summary>
        /// Gets the motor's velocity and servo settings.
        /// </summary>
        public MotorSettings3D Settings
        {
            get { return settings; }
        }

        #region I3DImpulseConstraintWithError Members

        /// <summary>
        /// Gets the current relative velocity between the connected entities with respect to the constraint.
        /// </summary>
        public FPVector3 RelativeVelocity
        {
            get
            {
                FPVector3 lambda;
                FPVector3.Cross(ref r, ref entity.angularVelocity, out lambda);
                FPVector3.Subtract(ref lambda, ref entity.linearVelocity, out lambda);
                return lambda;
            }
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
            get { return error; }
        }

        #endregion

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            //Compute relative velocity
            FPVector3 lambda;
            FPVector3.Cross(ref r, ref entity.angularVelocity, out lambda);
            FPVector3.Subtract(ref lambda, ref entity.linearVelocity, out lambda);

            //Add in bias velocity
            FPVector3.Add(ref biasVelocity, ref lambda, out lambda);

            //Add in softness
            FPVector3 softnessVelocity;
            FPVector3.Multiply(ref accumulatedImpulse, usedSoftness, out softnessVelocity);
            FPVector3.Subtract(ref lambda, ref softnessVelocity, out lambda);

            //In terms of an impulse (an instantaneous change in momentum), what is it?
            Matrix3x3.Transform(ref lambda, ref effectiveMassMatrix, out lambda);

            //Sum the impulse.
            FPVector3 previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse += lambda;

            //If the impulse it takes to get to the goal is too high for the motor to handle, scale it back.
            FP sumImpulseLengthSquared = accumulatedImpulse.LengthSquared();
            if (sumImpulseLengthSquared > maxForceDtSquared)
            {
                //max / impulse gives some value 0 < x < 1.  Basically, normalize the vector (divide by the length) and scale by the maximum.
                accumulatedImpulse *= maxForceDt / FP.Sqrt(sumImpulseLengthSquared);

                //Since the limit was exceeded by this corrective impulse, limit it so that the accumulated impulse remains constrained.
                lambda = accumulatedImpulse - previousAccumulatedImpulse;
            }


            entity.ApplyLinearImpulse(ref lambda);
            FPVector3 taImpulse;
            FPVector3.Cross(ref r, ref lambda, out taImpulse);
            entity.ApplyAngularImpulse(ref taImpulse);

            return (FP.Abs(lambda.x) + FP.Abs(lambda.y) + FP.Abs(lambda.z));
        }

        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {
            //Transform point into world space.
            Matrix3x3.Transform(ref localPoint, ref entity.orientationMatrix, out r);
            FPVector3.Add(ref r, ref entity.position, out worldPoint);

            FP updateRate = F64.C1 / dt;
            if (settings.mode == MotorMode.Servomechanism)
            {
                FPVector3.Subtract(ref settings.servo.goal, ref worldPoint, out error);
                FP separationDistance = error.Length();
                if (separationDistance > Toolbox.BigEpsilon)
                {
                    FP errorReduction;
                    settings.servo.springSettings.ComputeErrorReductionAndSoftness(dt, updateRate, out errorReduction, out usedSoftness);

                    //The rate of correction can be based on a constant correction velocity as well as a 'spring like' correction velocity.
                    //The constant correction velocity could overshoot the destination, so clamp it.
                    FP correctionSpeed = MathHelper.Min(settings.servo.baseCorrectiveSpeed, separationDistance * updateRate) +
                                            separationDistance * errorReduction;

                    FPVector3.Multiply(ref error, correctionSpeed / separationDistance, out biasVelocity);
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
                    //Wouldn't want to use a bias from an earlier frame.
                    biasVelocity = new FPVector3();
                }
            }
            else
            {
                usedSoftness = settings.velocityMotor.softness * updateRate;
                biasVelocity = settings.velocityMotor.goalVelocity;
                error = FPVector3.zero;
            }

            //Compute the maximum force that can be applied this frame.
            ComputeMaxForces(settings.maximumForce, dt);

            //COMPUTE EFFECTIVE MASS MATRIX
            //Transforms a change in velocity to a change in momentum when multiplied.
            Matrix3x3 linearComponent;
            Matrix3x3.CreateScale(entity.inverseMass, out linearComponent);
            Matrix3x3 rACrossProduct;
            Matrix3x3.CreateCrossProduct(ref r, out rACrossProduct);
            Matrix3x3 angularComponentA;
            Matrix3x3.Multiply(ref rACrossProduct, ref entity.inertiaTensorInverse, out angularComponentA);
            Matrix3x3.Multiply(ref angularComponentA, ref rACrossProduct, out angularComponentA);
            Matrix3x3.Subtract(ref linearComponent, ref angularComponentA, out effectiveMassMatrix);

            effectiveMassMatrix.M11 += usedSoftness;
            effectiveMassMatrix.M22 += usedSoftness;
            effectiveMassMatrix.M33 += usedSoftness;

            Matrix3x3.Invert(ref effectiveMassMatrix, out effectiveMassMatrix);

        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
        {
            //"Warm start" the constraint by applying a first guess of the solution should be.
            entity.ApplyLinearImpulse(ref accumulatedImpulse);
            FPVector3 taImpulse;
            FPVector3.Cross(ref r, ref accumulatedImpulse, out taImpulse);
            entity.ApplyAngularImpulse(ref taImpulse);
        }

        /// <summary>
        /// Computes the maxForceDt and maxForceDtSquared fields.
        /// </summary>
        private void ComputeMaxForces(FP maxForce, FP dt)
        {
            //Determine maximum force
            if (maxForce < FP.MaxValue)
            {
                maxForceDt = maxForce * dt;
                maxForceDtSquared = maxForceDt * maxForceDt;
            }
            else
            {
                maxForceDt = FP.MaxValue;
                maxForceDtSquared = FP.MaxValue;
            }
        }
    }
}