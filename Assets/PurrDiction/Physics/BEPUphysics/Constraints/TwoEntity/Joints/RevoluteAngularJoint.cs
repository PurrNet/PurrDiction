using System;
using BEPUphysics.Entities;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Joints
{
    /// <summary>
    /// Constrains two entities to rotate only around a single axis.
    /// Acts like the angular portion of a hinge joint.
    /// </summary>
    public class RevoluteAngularJoint : Joint, I2DImpulseConstraintWithError, I2DJacobianConstraint
    {
        private FPVector2 accumulatedImpulse;
        private FPVector2 biasVelocity;
        private Matrix2x2 effectiveMassMatrix;
        private FPVector3 localAxisA, localAxisB;
        private FPVector3 localConstrainedAxis1, localConstrainedAxis2; //Not a and b because they are both based on a...
        private FPVector2 error;
        private FPVector3 worldAxisA, worldAxisB;
        private FPVector3 worldConstrainedAxis1, worldConstrainedAxis2;

        /// <summary>
        /// Constructs a new orientation joint.
        /// Orientation joints can be used to simulate the angular portion of a hinge.
        /// Orientation joints allow rotation around only a single axis.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the WorldFreeAxisA and WorldFreeAxisB (or their entity-local versions).
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public RevoluteAngularJoint()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a new orientation joint.
        /// Orientation joints can be used to simulate the angular portion of a hinge.
        /// Orientation joints allow rotation around only a single axis.
        /// </summary>
        /// <param name="connectionA">First entity connected in the orientation joint.</param>
        /// <param name="connectionB">Second entity connected in the orientation joint.</param>
        /// <param name="freeAxis">Axis allowed to rotate freely in world space.</param>
        public RevoluteAngularJoint(Entity connectionA, Entity connectionB, FPVector3 freeAxis)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            //rA and rB store the local version of the axis.
            WorldFreeAxisA = freeAxis;
            WorldFreeAxisB = freeAxis;
        }

        /// <summary>
        /// Gets or sets the free axis in connection A's local space.
        /// Updates the internal restricted axes.
        /// </summary>
        public FPVector3 LocalFreeAxisA
        {
            get { return localAxisA; }
            set
            {
                localAxisA = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localAxisA, ref connectionA._orientationMatrix, out worldAxisA);
                UpdateRestrictedAxes();
            }
        }

        /// <summary>
        /// Gets or sets the free axis in connection B's local space.
        /// </summary>
        public FPVector3 LocalFreeAxisB
        {
            get { return localAxisB; }
            set
            {
                localAxisB = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localAxisB, ref connectionB._orientationMatrix, out worldAxisB);
            }
        }

        /// <summary>
        /// Gets or sets the free axis attached to connection A in world space.
        /// This does not change the other connection's free axis.
        /// Updates the internal restricted axes.
        /// </summary>
        public FPVector3 WorldFreeAxisA
        {
            get { return worldAxisA; }
            set
            {
                worldAxisA = FPVector3.Normalize(value);
                Matrix3x3.TransformTranspose(ref worldAxisA, ref connectionA._orientationMatrix, out localAxisA);
                UpdateRestrictedAxes();
            }
        }

        /// <summary>
        /// Gets or sets the free axis attached to connection A in world space.
        /// This does not change the other connection's free axis.
        /// </summary>
        public FPVector3 WorldFreeAxisB
        {
            get { return worldAxisB; }
            set
            {
                worldAxisB = FPVector3.Normalize(value);
                Matrix3x3.TransformTranspose(ref worldAxisB, ref connectionB._orientationMatrix, out localAxisB);
            }
        }

        private void UpdateRestrictedAxes()
        {
            localConstrainedAxis1 = FPVector3.Cross(FPVector3.up, localAxisA);
            if (localConstrainedAxis1.LengthSquared() < F64.C0p001)
            {
                localConstrainedAxis1 = FPVector3.Cross(FPVector3.right, localAxisA);
            }
            localConstrainedAxis2 = FPVector3.Cross(localAxisA, localConstrainedAxis1);
            localConstrainedAxis1.Normalize();
            localConstrainedAxis2.Normalize();
        }

        #region I2DImpulseConstraintWithError Members

        /// <summary>
        /// Gets the current relative velocity between the connected entities with respect to the constraint.
        /// </summary>
        public FPVector2 RelativeVelocity
        {
            get
            {
                FPVector3 velocity;
                FPVector3.Subtract(ref connectionA._angularVelocity, ref connectionB._angularVelocity, out velocity);

#if !WINDOWS
                FPVector2 lambda = new FPVector2();
#else
                Vector2 lambda;
#endif
                FPVector3.Dot(ref worldConstrainedAxis1, ref velocity, out lambda.x);
                FPVector3.Dot(ref worldConstrainedAxis2, ref velocity, out lambda.y);
                return lambda;
            }
        }

        /// <summary>
        /// Gets the total impulse applied by this constraint.
        /// </summary>
        public FPVector2 TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the current constraint error.
        /// </summary>
        public FPVector2 Error
        {
            get { return error; }
        }

        #endregion

        #region I2DJacobianConstraint Members

        /// <summary>
        /// Gets the linear jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobianX">First linear jacobian entry for the first connected entity.</param>
        /// <param name="jacobianY">Second linear jacobian entry for the first connected entity.</param>
        public void GetLinearJacobianA(out FPVector3 jacobianX, out FPVector3 jacobianY)
        {
            jacobianX = Toolbox.ZeroVector;
            jacobianY = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the linear jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobianX">First linear jacobian entry for the second connected entity.</param>
        /// <param name="jacobianY">Second linear jacobian entry for the second connected entity.</param>
        public void GetLinearJacobianB(out FPVector3 jacobianX, out FPVector3 jacobianY)
        {
            jacobianX = Toolbox.ZeroVector;
            jacobianY = Toolbox.ZeroVector;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the first connected entity.
        /// </summary>
        /// <param name="jacobianX">First angular jacobian entry for the first connected entity.</param>
        /// <param name="jacobianY">Second angular jacobian entry for the first connected entity.</param>
        public void GetAngularJacobianA(out FPVector3 jacobianX, out FPVector3 jacobianY)
        {
            jacobianX = worldConstrainedAxis1;
            jacobianY = worldConstrainedAxis2;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobianX">First angular jacobian entry for the second connected entity.</param>
        /// <param name="jacobianY">Second angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobianX, out FPVector3 jacobianY)
        {
            jacobianX = -worldConstrainedAxis1;
            jacobianY = -worldConstrainedAxis2;
        }

        /// <summary>
        /// Gets the mass matrix of the constraint.
        /// </summary>
        /// <param name="massMatrix">Constraint's mass matrix.</param>
        public void GetMassMatrix(out Matrix2x2 massMatrix)
        {
            massMatrix = effectiveMassMatrix;
        }

        #endregion

        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {
            Matrix3x3.Transform(ref localAxisA, ref connectionA._orientationMatrix, out worldAxisA);
            Matrix3x3.Transform(ref localAxisB, ref connectionB._orientationMatrix, out worldAxisB);


            Matrix3x3.Transform(ref localConstrainedAxis1, ref connectionA._orientationMatrix, out worldConstrainedAxis1);
            Matrix3x3.Transform(ref localConstrainedAxis2, ref connectionA._orientationMatrix, out worldConstrainedAxis2);

            FPVector3 error;
            FPVector3.Cross(ref worldAxisA, ref worldAxisB, out error);

            FPVector3.Dot(ref error, ref worldConstrainedAxis1, out this.error.x);
            FPVector3.Dot(ref error, ref worldConstrainedAxis2, out this.error.y);
            FP errorReduction;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReduction, out softness);
            errorReduction = -errorReduction;
            biasVelocity.x = errorReduction * this.error.x;
            biasVelocity.y = errorReduction * this.error.y;


            //Ensure that the corrective velocity doesn't exceed the max.
            FP length = biasVelocity.LengthSquared();
            if (length > maxCorrectiveVelocitySquared)
            {
                FP multiplier = maxCorrectiveVelocity / FP.Sqrt(length);
                biasVelocity.x *= multiplier;
                biasVelocity.y *= multiplier;
            }

            FPVector3 axis1I, axis2I;
            if (connectionA._isDynamic && connectionB._isDynamic)
            {
                Matrix3x3 inertiaTensorSum;
                Matrix3x3.Add(ref connectionA.inertiaTensorInverse, ref connectionB.inertiaTensorInverse, out inertiaTensorSum);

                Matrix3x3.Transform(ref worldConstrainedAxis1, ref inertiaTensorSum, out axis1I);
                Matrix3x3.Transform(ref worldConstrainedAxis2, ref inertiaTensorSum, out axis2I);
            }
            else if (connectionA._isDynamic && !connectionB._isDynamic)
            {
                Matrix3x3.Transform(ref worldConstrainedAxis1, ref connectionA.inertiaTensorInverse, out axis1I);
                Matrix3x3.Transform(ref worldConstrainedAxis2, ref connectionA.inertiaTensorInverse, out axis2I);
            }
            else if (!connectionA._isDynamic && connectionB._isDynamic)
            {
                Matrix3x3.Transform(ref worldConstrainedAxis1, ref connectionB.inertiaTensorInverse, out axis1I);
                Matrix3x3.Transform(ref worldConstrainedAxis2, ref connectionB.inertiaTensorInverse, out axis2I);
            }
            else
            {
                throw new InvalidOperationException($"Cannot constrain two kinematic bodies.");
            }

            FPVector3.Dot(ref axis1I, ref worldConstrainedAxis1, out effectiveMassMatrix.M11);
            FPVector3.Dot(ref axis1I, ref worldConstrainedAxis2, out effectiveMassMatrix.M12);
            FPVector3.Dot(ref axis2I, ref worldConstrainedAxis1, out effectiveMassMatrix.M21);
            FPVector3.Dot(ref axis2I, ref worldConstrainedAxis2, out effectiveMassMatrix.M22);
            effectiveMassMatrix.M11 += softness;
            effectiveMassMatrix.M22 += softness;
            Matrix2x2.Invert(ref effectiveMassMatrix, out effectiveMassMatrix);
            Matrix2x2.Negate(ref effectiveMassMatrix, out effectiveMassMatrix);


        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
        {
            //Warm Starting
#if !WINDOWS
            FPVector3 impulse = new FPVector3();
#else
            Vector3 impulse;
#endif
            impulse.x = worldConstrainedAxis1.x * accumulatedImpulse.x + worldConstrainedAxis2.x * accumulatedImpulse.y;
            impulse.y = worldConstrainedAxis1.y * accumulatedImpulse.x + worldConstrainedAxis2.y * accumulatedImpulse.y;
            impulse.z = worldConstrainedAxis1.z * accumulatedImpulse.x + worldConstrainedAxis2.z * accumulatedImpulse.y;
            if (connectionA._isDynamic)
            {
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Negate(ref impulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        }


        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            // lambda = -mc * (Jv + b)
            // P = JT * lambda
            FPVector3 velocity;
            FPVector3.Subtract(ref connectionA._angularVelocity, ref connectionB._angularVelocity, out velocity);

#if !WINDOWS
            FPVector2 lambda = new FPVector2();
#else
            Vector2 lambda;
#endif
            FPVector3.Dot(ref worldConstrainedAxis1, ref velocity, out lambda.x);
            FPVector3.Dot(ref worldConstrainedAxis2, ref velocity, out lambda.y);
            FPVector2.Add(ref lambda, ref biasVelocity, out lambda);
            FPVector2 softnessImpulse;
            FPVector2.Multiply(ref accumulatedImpulse, softness, out softnessImpulse);
            FPVector2.Add(ref lambda, ref softnessImpulse, out lambda);
            Matrix2x2.Transform(ref lambda, ref effectiveMassMatrix, out lambda);
            FPVector2.Add(ref accumulatedImpulse, ref lambda, out accumulatedImpulse);


#if !WINDOWS
            FPVector3 impulse = new FPVector3();
#else
            Vector3 impulse;
#endif
            impulse.x = worldConstrainedAxis1.x * lambda.x + worldConstrainedAxis2.x * lambda.y;
            impulse.y = worldConstrainedAxis1.y * lambda.x + worldConstrainedAxis2.y * lambda.y;
            impulse.z = worldConstrainedAxis1.z * lambda.x + worldConstrainedAxis2.z * lambda.y;
            if (connectionA._isDynamic)
            {
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Negate(ref impulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }

            return (FP.Abs(lambda.x) + FP.Abs(lambda.y));
        }


    }
}