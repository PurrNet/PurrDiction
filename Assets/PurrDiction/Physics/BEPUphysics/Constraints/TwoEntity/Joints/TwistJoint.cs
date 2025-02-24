using System;
using BEPUphysics.Entities;
 
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.TwoEntity.Joints
{
    /// <summary>
    /// Prevents the connected entities from twisting relative to each other.
    /// Acts like the angular part of a universal joint.
    /// </summary>
    public class TwistJoint : Joint, I1DImpulseConstraintWithError, I1DJacobianConstraint
    {
        private FPVector3 aLocalAxisY, aLocalAxisZ;
        private FP accumulatedImpulse;
        private FPVector3 bLocalAxisY;
        private FP biasVelocity;
        private FPVector3 jacobianA, jacobianB;
        private FP error;
        private FPVector3 localAxisA;
        private FPVector3 localAxisB;
        private FPVector3 worldAxisA;
        private FPVector3 worldAxisB;
        private FP velocityToImpulse;

        /// <summary>
        /// Constructs a new constraint which prevents the connected entities from twisting relative to each other.
        /// To finish the initialization, specify the connections (ConnectionA and ConnectionB) 
        /// as well as the WorldAxisA and WorldAxisB (or their entity-local versions).
        /// This constructor sets the constraint's IsActive property to false by default.
        /// </summary>
        public TwistJoint()
        {
            IsActive = false;
        }

        /// <summary>
        /// Constructs a new constraint which prevents the connected entities from twisting relative to each other.
        /// </summary>
        /// <param name="connectionA">First connection of the pair.</param>
        /// <param name="connectionB">Second connection of the pair.</param>
        /// <param name="axisA">Twist axis attached to the first connected entity.</param>
        /// <param name="axisB">Twist axis attached to the second connected entity.</param>
        public TwistJoint(Entity connectionA, Entity connectionB, FPVector3 axisA, FPVector3 axisB)
        {
            ConnectionA = connectionA;
            ConnectionB = connectionB;
            WorldAxisA = axisA;
            WorldAxisB = axisB;
        }

        /// <summary>
        /// Gets or sets the axis attached to the first connected entity in its local space.
        /// </summary>
        public FPVector3 LocalAxisA
        {
            get { return localAxisA; }
            set
            {
                localAxisA = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localAxisA, ref connectionA._orientationMatrix, out worldAxisA);
                Initialize();
            }
        }

        /// <summary>
        /// Gets or sets the axis attached to the first connected entity in its local space.
        /// </summary>
        public FPVector3 LocalAxisB
        {
            get { return localAxisB; }
            set
            {
                localAxisB = FPVector3.Normalize(value);
                Matrix3x3.Transform(ref localAxisB, ref connectionA._orientationMatrix, out worldAxisB);
                Initialize();
            }
        }

        /// <summary>
        /// Gets or sets the axis attached to the first connected entity in world space.
        /// </summary>
        public FPVector3 WorldAxisA
        {
            get { return worldAxisA; }
            set
            {
                worldAxisA = FPVector3.Normalize(value);
                FPQuaternion conjugate;
                FPQuaternion.Conjugate(ref connectionA._orientation, out conjugate);
                FPQuaternion.Transform(ref worldAxisA, ref conjugate, out localAxisA);
                Initialize();
            }
        }

        /// <summary>
        /// Gets or sets the axis attached to the first connected entity in world space.
        /// </summary>
        public FPVector3 WorldAxisB
        {
            get { return worldAxisB; }
            set
            {
                worldAxisB = FPVector3.Normalize(value);
                FPQuaternion conjugate;
                FPQuaternion.Conjugate(ref connectionB._orientation, out conjugate);
                FPQuaternion.Transform(ref worldAxisB, ref conjugate, out localAxisB);
                Initialize();
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
                FPVector3.Dot(ref connectionA._angularVelocity, ref jacobianA, out velocityA);
                FPVector3.Dot(ref connectionB._angularVelocity, ref jacobianB, out velocityB);
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
        /// Solves for velocity.
        /// </summary>
        public override FP SolveIteration()
        {
            FP velocityA, velocityB;
            //Find the velocity contribution from each connection
            FPVector3.Dot(ref connectionA._angularVelocity, ref jacobianA, out velocityA);
            FPVector3.Dot(ref connectionB._angularVelocity, ref jacobianB, out velocityB);
            //Add in the constraint space bias velocity
            FP lambda = -(velocityA + velocityB) + biasVelocity - softness * accumulatedImpulse;

            //Transform to an impulse
            lambda *= velocityToImpulse;

            //Accumulate the impulse
            accumulatedImpulse += lambda;

            //Apply the impulse
            FPVector3 impulse;
            if (connectionA._isDynamic)
            {
                FPVector3.Multiply(ref jacobianA, lambda, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Multiply(ref jacobianB, lambda, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }

            return (FP.Abs(lambda));
        }

        /// <summary>
        /// Do any necessary computations to prepare the constraint for this frame.
        /// </summary>
        /// <param name="dt">Simulation step length.</param>
        public override void Update(FP dt)
        {
            FPVector3 aAxisY, aAxisZ;
            FPVector3 bAxisY;
            Matrix3x3.Transform(ref localAxisA, ref connectionA._orientationMatrix, out worldAxisA);
            Matrix3x3.Transform(ref aLocalAxisY, ref connectionA._orientationMatrix, out aAxisY);
            Matrix3x3.Transform(ref aLocalAxisZ, ref connectionA._orientationMatrix, out aAxisZ);
            Matrix3x3.Transform(ref localAxisB, ref connectionB._orientationMatrix, out worldAxisB);
            Matrix3x3.Transform(ref bLocalAxisY, ref connectionB._orientationMatrix, out bAxisY);

            FPQuaternion rotation;
            FPQuaternion.GetQuaternionBetweenNormalizedVectors(ref worldAxisB, ref worldAxisA, out rotation);

            //Transform b's 'Y' axis so that it is perpendicular with a's 'X' axis for measurement.
            FPVector3 twistMeasureAxis;
            FPQuaternion.Transform(ref bAxisY, ref rotation, out twistMeasureAxis);

            //By dotting the measurement vector with a 2d plane's axes, we can get a local X and Y value.
            FP y, x;
            FPVector3.Dot(ref twistMeasureAxis, ref aAxisZ, out y);
            FPVector3.Dot(ref twistMeasureAxis, ref aAxisY, out x);
            error = FP.FastAtan2(y, x);

            //Debug.WriteLine("Angle: " + angle);

            //The nice thing about this approach is that the jacobian entry doesn't flip.
            //Instead, the error can be negative due to the use of Atan2.
            //This is important for limits which have a unique high and low value.

            //Compute the jacobian.
            FPVector3.Add(ref worldAxisA, ref worldAxisB, out jacobianB);
            if (jacobianB.LengthSquared() < Toolbox.Epsilon)
            {
                //A nasty singularity can show up if the axes are aligned perfectly.
                //In a 'real' situation, this is impossible, so just ignore it.
                isActiveInSolver = false;
                return;
            }

            jacobianB.Normalize();
            jacobianA.x = -jacobianB.x;
            jacobianA.y = -jacobianB.y;
            jacobianA.z = -jacobianB.z;

            //****** VELOCITY BIAS ******//
            //Compute the correction velocity.
            FP errorReduction;
            springSettings.ComputeErrorReductionAndSoftness(dt, F64.C1 / dt, out errorReduction, out softness);
            biasVelocity = MathHelper.Clamp(-error * errorReduction, -maxCorrectiveVelocity, maxCorrectiveVelocity);

            //****** EFFECTIVE MASS MATRIX ******//
            //Connection A's contribution to the mass matrix
            FP entryA;
            FPVector3 transformedAxis;
            if (connectionA._isDynamic)
            {
                Matrix3x3.Transform(ref jacobianA, ref connectionA.inertiaTensorInverse, out transformedAxis);
                FPVector3.Dot(ref transformedAxis, ref jacobianA, out entryA);
            }
            else
                entryA = F64.C0;

            //Connection B's contribution to the mass matrix
            FP entryB;
            if (connectionB._isDynamic)
            {
                Matrix3x3.Transform(ref jacobianB, ref connectionB.inertiaTensorInverse, out transformedAxis);
                FPVector3.Dot(ref transformedAxis, ref jacobianB, out entryB);
            }
            else
                entryB = F64.C0;

            //Compute the inverse mass matrix
            velocityToImpulse = F64.C1 / (softness + entryA + entryB);

            
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
            if (connectionA._isDynamic)
            {
                FPVector3.Multiply(ref jacobianA, accumulatedImpulse, out impulse);
                connectionA.ApplyAngularImpulse(ref impulse);
            }
            if (connectionB._isDynamic)
            {
                FPVector3.Multiply(ref jacobianB, accumulatedImpulse, out impulse);
                connectionB.ApplyAngularImpulse(ref impulse);
            }
        }

        /// <summary>
        /// Computes the internal bases and target relative state based on the current axes and entity states.
        /// Called automatically when setting any of the axis properties.
        /// </summary>
        public void Initialize()
        {
            //Compute a vector which is perpendicular to the axis.  It'll be added in local space to both connections.
            FPVector3 yAxis;
            FPVector3.Cross(ref worldAxisA, ref Toolbox.UpVector, out yAxis);
            FP length = yAxis.LengthSquared();
            if (length < Toolbox.Epsilon)
            {
                FPVector3.Cross(ref worldAxisA, ref Toolbox.RightVector, out yAxis);
            }
            yAxis.Normalize();

            //Put the axis into the local space of A.
            FPQuaternion conjugate;
            FPQuaternion.Conjugate(ref connectionA._orientation, out conjugate);
            FPQuaternion.Transform(ref yAxis, ref conjugate, out aLocalAxisY);

            //Complete A's basis.
            FPVector3.Cross(ref localAxisA, ref aLocalAxisY, out aLocalAxisZ);

            //Rotate the axis to B since it could be arbitrarily rotated.
            FPQuaternion rotation;
            FPQuaternion.GetQuaternionBetweenNormalizedVectors(ref worldAxisA, ref worldAxisB, out rotation);
            FPQuaternion.Transform(ref yAxis, ref rotation, out yAxis);

            //Put it into local space.
            FPQuaternion.Conjugate(ref connectionB._orientation, out conjugate);
            FPQuaternion.Transform(ref yAxis, ref conjugate, out bLocalAxisY);
        }
    }
}