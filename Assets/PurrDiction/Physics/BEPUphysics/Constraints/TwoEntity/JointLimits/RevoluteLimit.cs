using System;
using BEPUphysics.Entities;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.JointLimits
{
    /// <summary>
    /// Constraint which prevents the connected entities from rotating relative to each other around an axis beyond given limits.
    /// </summary>
    public class RevoluteLimit : JointLimit, I2DImpulseConstraintWithError, I2DJacobianConstraint
    {
        private readonly JointBasis2D basis = new JointBasis2D();

        private FPVector2 accumulatedImpulse;
        private FPVector2 biasVelocity;
        private FPVector3 jacobianMaxA;
        private FPVector3 jacobianMaxB;
        private FPVector3 jacobianMinA;
        private FPVector3 jacobianMinB;
        private bool maxIsActive;
        private bool minIsActive;
        private FPVector2 error;
        private FPVector3 localTestAxis;

        /// <summary>
        /// Naximum angle that entities can twist.
        /// </summary>
        protected FP maximumAngle;

        /// <summary>
        /// Minimum angle that entities can twist.
        /// </summary>
        protected FP minimumAngle;

        private FPVector3 worldTestAxis;
        private FPVector2 velocityToImpulse;

        /// <summary>
        /// Constructs a new constraint which prevents the connected entities from rotating relative to each other around an axis beyond given limits.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the TestAxis (or its entity-local version) and the Basis.
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public RevoluteLimit()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a new constraint which prevents the connected entities from rotating relative to each other around an axis beyond given limits.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        /// <param name="limitedAxis">Axis of rotation to be limited.</param>
        /// <param name="testAxis">Axis attached to connectionB that is tested to determine the current angle.
        /// Will also be used as the base rotation axis representing 0 degrees.</param>
        /// <param name="minimumAngle">Minimum twist angle allowed.</param>
        /// <param name="maximumAngle">Maximum twist angle allowed.</param>
        public RevoluteLimit(Entity connectionA, Entity connectionB, FPVector3 limitedAxis, FPVector3 testAxis, FP minimumAngle, FP maximumAngle)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;

            //Put the axes into the joint transform of A.
            basis.rotationMatrix = this.connectionA.orientationMatrix;
            basis.SetWorldAxes(limitedAxis, testAxis);

            //Put the axes into the 'joint transform' of B too.
            TestAxis = basis.xAxis;

            MinimumAngle = minimumAngle;
            MaximumAngle = maximumAngle;
        }


        /// <summary>
        /// Constructs a new constraint which prevents the connected entities from rotating relative to each other around an axis beyond given limits.
        /// Using this constructor will leave the limit uninitialized.  Before using the limit in a simulation, be sure to set the basis axes using
        /// Basis.SetLocalAxes or Basis.SetWorldAxes and the test axis using the LocalTestAxis or TestAxis properties.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        public RevoluteLimit(Entity connectionA, Entity connectionB)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
        }

        /// <summary>
        /// Gets the basis attached to entity A.
        /// The primary axis represents the limited axis of rotation.  The 'measurement plane' which the test axis is tested against is based on this primary axis.
        /// The x axis defines the 'base' direction on the measurement plane corresponding to 0 degrees of relative rotation.
        /// </summary>
        public JointBasis2D Basis
        {
            get { return basis; }
        }

        /// <summary>
        /// Gets or sets the axis attached to entity B in its local space that will be tested against the limits.
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
        /// Gets or sets the maximum angle that entities can twist.
        /// </summary>
        public FP MaximumAngle
        {
            get { return maximumAngle; }
            set
            {
                maximumAngle = value % (MathHelper.TwoPi);
                if (minimumAngle > MathHelper.Pi)
                    minimumAngle -= MathHelper.TwoPi;
                if (minimumAngle <= -MathHelper.Pi)
                    minimumAngle += MathHelper.TwoPi;
            }
        }

        /// <summary>
        /// Gets or sets the minimum angle that entities can twist.
        /// </summary>
        public FP MinimumAngle
        {
            get { return minimumAngle; }
            set
            {
                minimumAngle = value % (MathHelper.TwoPi);
                if (minimumAngle > MathHelper.Pi)
                    minimumAngle -= MathHelper.TwoPi;
                if (minimumAngle <= -MathHelper.Pi)
                    minimumAngle += MathHelper.TwoPi;
            }
        }

        /// <summary>
        /// Gets or sets the axis attached to entity B in world space that will be tested against the limits.
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

        #region I2DImpulseConstraintWithError Members

        /// <summary>
        /// Gets the current relative velocity between the connected entities with respect to the constraint.
        /// The revolute limit is special; internally, it is sometimes two constraints.
        /// The X value of the vector is the "minimum" plane of the limit, and the Y value is the "maximum" plane.
        /// If a plane isn't active, its error is zero.
        /// </summary>
        public FPVector2 RelativeVelocity
        {
            get
            {
                if (isLimitActive)
                {
                    FP velocityA, velocityB;
                    FPVector2 toReturn = FPVector2.Zero;
                    if (minIsActive)
                    {
                        FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMinA, out velocityA);
                        FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMinB, out velocityB);
                        toReturn.x = velocityA + velocityB;
                    }
                    if (maxIsActive)
                    {
                        FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMaxA, out velocityA);
                        FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMaxB, out velocityB);
                        toReturn.y = velocityA + velocityB;
                    }
                    return toReturn;
                }
                return new FPVector2();
            }
        }

        /// <summary>
        /// Gets the total impulse applied by this constraint.
        /// The x component corresponds to the minimum plane limit,
        /// while the y component corresponds to the maximum plane limit.
        /// </summary>
        public FPVector2 TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the current constraint error.
        /// The x component corresponds to the minimum plane limit,
        /// while the y component corresponds to the maximum plane limit.
        /// </summary>
        public FPVector2 Error
        {
            get { return error; }
        }

        #endregion

        //Newer version of revolute limit will use up to two planes.  This is sort of like being a double-constraint, with two jacobians and everything.
        //Not going to solve both plane limits simultaneously because they can be redundant.  De-linking them will let the system deal with redundancy better.

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
            jacobianX = jacobianMinA;
            jacobianY = jacobianMaxA;
        }

        /// <summary>
        /// Gets the angular jacobian entry for the second connected entity.
        /// </summary>
        /// <param name="jacobianX">First angular jacobian entry for the second connected entity.</param>
        /// <param name="jacobianY">Second angular jacobian entry for the second connected entity.</param>
        public void GetAngularJacobianB(out FPVector3 jacobianX, out FPVector3 jacobianY)
        {
            jacobianX = jacobianMinB;
            jacobianY = jacobianMaxB;
        }

        /// <summary>
        /// Gets the mass matrix of the revolute limit.
        /// The revolute limit is special; in terms of solving, it is
        /// actually sometimes TWO constraints; a minimum plane, and a
        /// maximum plane.  The M11 field represents the minimum plane mass matrix
        /// and the M22 field represents the maximum plane mass matrix.
        /// </summary>
        /// <param name="massMatrix">Mass matrix of the constraint.</param>
        public void GetMassMatrix(out Matrix2x2 massMatrix)
        {
            massMatrix.M11 = velocityToImpulse.x;
            massMatrix.M22 = velocityToImpulse.y;
            massMatrix.M12 = F64.C0;
            massMatrix.M21 = F64.C0;
        }

        #endregion

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            FP lambda;
            FP lambdaTotal = F64.C0;
            FP velocityA, velocityB;
            FP previousAccumulatedImpulse;
            if (minIsActive)
            {
                //Find the velocity contribution from each connection
                FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMinA, out velocityA);
                FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMinB, out velocityB);
                //Add in the constraint space bias velocity
                lambda = -(velocityA + velocityB) + biasVelocity.x - softness * accumulatedImpulse.x;

                //Transform to an impulse
                lambda *= velocityToImpulse.x;

                //Clamp accumulated impulse (can't go negative)
                previousAccumulatedImpulse = accumulatedImpulse.x;
                accumulatedImpulse.x = MathHelper.Max(accumulatedImpulse.x + lambda, F64.C0);
                lambda = accumulatedImpulse.x - previousAccumulatedImpulse;

                //Apply the impulse
                FPVector3 impulse;
                if (connectionA.isDynamic)
                {
                    FPVector3.Multiply(ref jacobianMinA, lambda, out impulse);
                    connectionA.ApplyAngularImpulse(ref impulse);
                }
                if (connectionB.isDynamic)
                {
                    FPVector3.Multiply(ref jacobianMinB, lambda, out impulse);
                    connectionB.ApplyAngularImpulse(ref impulse);
                }

                lambdaTotal += FP.Abs(lambda);
            }
            if (maxIsActive)
            {
                //Find the velocity contribution from each connection
                FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMaxA, out velocityA);
                FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMaxB, out velocityB);
                //Add in the constraint space bias velocity
                lambda = -(velocityA + velocityB) + biasVelocity.y - softness * accumulatedImpulse.y;

                //Transform to an impulse
                lambda *= velocityToImpulse.y;

                //Clamp accumulated impulse (can't go negative)
                previousAccumulatedImpulse = accumulatedImpulse.y;
                accumulatedImpulse.y = MathHelper.Max(accumulatedImpulse.y + lambda, F64.C0);
                lambda = accumulatedImpulse.y - previousAccumulatedImpulse;

                //Apply the impulse
                FPVector3 impulse;
                if (connectionA.isDynamic)
                {
                    FPVector3.Multiply(ref jacobianMaxA, lambda, out impulse);
                    connectionA.ApplyAngularImpulse(ref impulse);
                }
                if (connectionB.isDynamic)
                {
                    FPVector3.Multiply(ref jacobianMaxB, lambda, out impulse);
                    connectionB.ApplyAngularImpulse(ref impulse);
                }

                lambdaTotal += FP.Abs(lambda);
            }
            return lambdaTotal;
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

            //Compute the plane normals.
            FPVector3 minPlaneNormal, maxPlaneNormal;
            //Rotate basisA y axis around the basisA primary axis.
            Matrix3x3 rotation;
            Matrix3x3.CreateFromAxisAngle(ref basis.primaryAxis, minimumAngle + MathHelper.PiOver2, out rotation);
            Matrix3x3.Transform(ref basis.xAxis, ref rotation, out minPlaneNormal);
            Matrix3x3.CreateFromAxisAngle(ref basis.primaryAxis, maximumAngle - MathHelper.PiOver2, out rotation);
            Matrix3x3.Transform(ref basis.xAxis, ref rotation, out maxPlaneNormal);

            //Compute the errors along the two normals.
            FP planePositionMin, planePositionMax;
            FPVector3.Dot(ref minPlaneNormal, ref worldTestAxis, out planePositionMin);
            FPVector3.Dot(ref maxPlaneNormal, ref worldTestAxis, out planePositionMax);


            FP span = GetDistanceFromMinimum(maximumAngle);

            //Early out and compute the determine the plane normal.
            if (span >= MathHelper.Pi)
            {
                if (planePositionMax > F64.C0 || planePositionMin > F64.C0)
                {
                    //It's in a perfectly valid configuration, so skip.
                    isActiveInSolver = false;
                    minIsActive = false;
                    maxIsActive = false;
                    error = FPVector2.Zero;
                    accumulatedImpulse = FPVector2.Zero;
                    isLimitActive = false;
                    return;
                }

                if (planePositionMax > planePositionMin)
                {
                    //It's quicker to escape out to the max plane than the min plane.
                    error.x = F64.C0;
                    error.y = -planePositionMax;
                    accumulatedImpulse.x = F64.C0;
                    minIsActive = false;
                    maxIsActive = true;
                }
                else
                {
                    //It's quicker to escape out to the min plane than the max plane.
                    error.x = -planePositionMin;
                    error.y = F64.C0;
                    accumulatedImpulse.y = F64.C0;
                    minIsActive = true;
                    maxIsActive = false;
                }
                //There's never a non-degenerate situation where having both planes active with a span 
                //greater than pi is useful.
            }
            else
            {
                if (planePositionMax > F64.C0 && planePositionMin > F64.C0)
                {
                    //It's in a perfectly valid configuration, so skip.
                    isActiveInSolver = false;
                    minIsActive = false;
                    maxIsActive = false;
                    error = FPVector2.Zero;
                    accumulatedImpulse = FPVector2.Zero;
                    isLimitActive = false;
                    return;
                }

                if (planePositionMin <= F64.C0 && planePositionMax <= F64.C0)
                {
                    //Escape upward.
                    //Activate both planes.
                    error.x = -planePositionMin;
                    error.y = -planePositionMax;
                    minIsActive = true;
                    maxIsActive = true;
                }
                else if (planePositionMin <= F64.C0)
                {
                    //It's quicker to escape out to the min plane than the max plane.
                    error.x = -planePositionMin;
                    error.y = F64.C0;
                    accumulatedImpulse.y = F64.C0;
                    minIsActive = true;
                    maxIsActive = false;
                }
                else
                {
                    //It's quicker to escape out to the max plane than the min plane.
                    error.x = F64.C0;
                    error.y = -planePositionMax;
                    accumulatedImpulse.x = F64.C0;
                    minIsActive = false;
                    maxIsActive = true;
                }
            }
            isLimitActive = true;


            //****** VELOCITY BIAS ******//
            //Compute the correction velocity
            FP errorReduction;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReduction, out softness);

            //Compute the jacobians
            if (minIsActive)
            {
                FPVector3.Cross(ref minPlaneNormal, ref worldTestAxis, out jacobianMinA);
                if (jacobianMinA.LengthSquared() < Toolbox.Epsilon)
                {
                    //The plane normal is aligned with the test axis.
                    //Use the basis's free axis.
                    jacobianMinA = basis.primaryAxis;
                }
                jacobianMinA.Normalize();
                jacobianMinB.x = -jacobianMinA.x;
                jacobianMinB.y = -jacobianMinA.y;
                jacobianMinB.z = -jacobianMinA.z;
            }
            if (maxIsActive)
            {
                FPVector3.Cross(ref maxPlaneNormal, ref worldTestAxis, out jacobianMaxA);
                if (jacobianMaxA.LengthSquared() < Toolbox.Epsilon)
                {
                    //The plane normal is aligned with the test axis.
                    //Use the basis's free axis.
                    jacobianMaxA = basis.primaryAxis;
                }
                jacobianMaxA.Normalize();
                jacobianMaxB.x = -jacobianMaxA.x;
                jacobianMaxB.y = -jacobianMaxA.y;
                jacobianMaxB.z = -jacobianMaxA.z;
            }

            //Error is always positive
            if (minIsActive)
            {
                biasVelocity.x = MathHelper.Min(MathHelper.Max(F64.C0, error.x - margin) * errorReduction, maxCorrectiveVelocity);
                if (bounciness > F64.C0)
                {
                    FP relativeVelocity;
                    FP dot;
                    //Find the velocity contribution from each connection
                    FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMinA, out relativeVelocity);
                    FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMinB, out dot);
                    relativeVelocity += dot;
                    biasVelocity.x = MathHelper.Max(biasVelocity.x, ComputeBounceVelocity(-relativeVelocity));
                }
            }
            if (maxIsActive)
            {
                biasVelocity.y = MathHelper.Min(MathHelper.Max(F64.C0, error.y - margin) * errorReduction, maxCorrectiveVelocity);
                if (bounciness > F64.C0)
                {
                    //Find the velocity contribution from each connection
                    if (maxIsActive)
                    {
                        FP relativeVelocity;
                        FPVector3.Dot(ref connectionA.angularVelocity, ref jacobianMaxA, out relativeVelocity);
                        FP dot;
                        FPVector3.Dot(ref connectionB.angularVelocity, ref jacobianMaxB, out dot);
                        relativeVelocity += dot;
                        biasVelocity.y = MathHelper.Max(biasVelocity.y, ComputeBounceVelocity(-relativeVelocity));
                    }
                }
            }


            //****** EFFECTIVE MASS MATRIX ******//
            //Connection A's contribution to the mass matrix
            FP minEntryA, minEntryB;
            FP maxEntryA, maxEntryB;
            FPVector3 transformedAxis;
            if (connectionA.isDynamic)
            {
                if (minIsActive)
                {
                    Matrix3x3.Transform(ref jacobianMinA, ref connectionA.inertiaTensorInverse, out transformedAxis);
                    FPVector3.Dot(ref transformedAxis, ref jacobianMinA, out minEntryA);
                }
                else
                    minEntryA = F64.C0;
                if (maxIsActive)
                {
                    Matrix3x3.Transform(ref jacobianMaxA, ref connectionA.inertiaTensorInverse, out transformedAxis);
                    FPVector3.Dot(ref transformedAxis, ref jacobianMaxA, out maxEntryA);
                }
                else
                    maxEntryA = F64.C0;
            }
            else
            {
                minEntryA = F64.C0;
                maxEntryA = F64.C0;
            }
            //Connection B's contribution to the mass matrix
            if (connectionB.isDynamic)
            {
                if (minIsActive)
                {
                    Matrix3x3.Transform(ref jacobianMinB, ref connectionB.inertiaTensorInverse, out transformedAxis);
                    FPVector3.Dot(ref transformedAxis, ref jacobianMinB, out minEntryB);
                }
                else
                    minEntryB = F64.C0;
                if (maxIsActive)
                {
                    Matrix3x3.Transform(ref jacobianMaxB, ref connectionB.inertiaTensorInverse, out transformedAxis);
                    FPVector3.Dot(ref transformedAxis, ref jacobianMaxB, out maxEntryB);
                }
                else
                    maxEntryB = F64.C0;
            }
            else
            {
                minEntryB = F64.C0;
                maxEntryB = F64.C0;
            }
            //Compute the inverse mass matrix
            //Notice that the mass matrix isn't linked, it's two separate ones.
            velocityToImpulse.x = F64.C1 / (softness + minEntryA + minEntryB);
            velocityToImpulse.y = F64.C1 / (softness + maxEntryA + maxEntryB);


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
            if (connectionA.isDynamic)
            {
                var impulse = new FPVector3();
                if (minIsActive)
                {
                    FPVector3.Multiply(ref jacobianMinA, accumulatedImpulse.x, out impulse);
                }
                if (maxIsActive)
                {
                    FPVector3 temp;
                    FPVector3.Multiply(ref jacobianMaxA, accumulatedImpulse.y, out temp);
                    FPVector3.Add(ref impulse, ref temp, out impulse);
                }
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB.isDynamic)
            {
                var impulse = new FPVector3();
                if (minIsActive)
                {
                    FPVector3.Multiply(ref jacobianMinB, accumulatedImpulse.x, out impulse);
                }
                if (maxIsActive)
                {
                    FPVector3 temp;
                    FPVector3.Multiply(ref jacobianMaxB, accumulatedImpulse.y, out temp);
                    FPVector3.Add(ref impulse, ref temp, out impulse);
                }
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        }

        private FP GetDistanceFromMinimum(FP angle)
        {
            if (minimumAngle > F64.C0)
            {
                if (angle >= minimumAngle)
                    return angle - minimumAngle;
                if (angle > F64.C0)
                    return MathHelper.TwoPi - minimumAngle + angle;
                return MathHelper.TwoPi - minimumAngle + angle;
            }
            if (angle < minimumAngle)
                return MathHelper.TwoPi - minimumAngle + angle;
            return angle - minimumAngle;
            //else //if (currentAngle >= 0)
            //    return angle - minimumAngle;
        }
    }
}