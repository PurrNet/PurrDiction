using BEPUphysics.Constraints;
using BEPUphysics.Entities;
 
using BEPUphysics.Materials;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Vehicle
{
    /// <summary>
    /// Handles a wheel's driving force for a vehicle.
    /// </summary>
    public class WheelDrivingMotor : ISolverSettings
    {
        #region Static Stuff

        /// <summary>
        /// Default blender used by WheelSlidingFriction constraints.
        /// </summary>
        public static WheelFrictionBlender DefaultGripFrictionBlender;

        static WheelDrivingMotor()
        {
            DefaultGripFrictionBlender = BlendFriction;
        }

        /// <summary>
        /// Function which takes the friction values from a wheel and a supporting material and computes the blended friction.
        /// </summary>
        /// <param name="wheelFriction">Friction coefficient associated with the wheel.</param>
        /// <param name="materialFriction">Friction coefficient associated with the support material.</param>
        /// <param name="usingKineticFriction">True if the friction coefficients passed into the blender are kinetic coefficients, false otherwise.</param>
        /// <param name="wheel">Wheel being blended.</param>
        /// <returns>Blended friction coefficient.</returns>
        public static FP BlendFriction(FP wheelFriction, FP materialFriction, bool usingKineticFriction, Wheel wheel)
        {
            return wheelFriction * materialFriction;
        }

        #endregion

        internal FP accumulatedImpulse;

        //Fix64 linearBX, linearBY, linearBZ;
        internal FP angularAX, angularAY, angularAZ;
        internal FP angularBX, angularBY, angularBZ;
        internal bool isActive = true;
        internal FP linearAX, linearAY, linearAZ;
        private FP currentFrictionCoefficient;
        internal FPVector3 forceAxis;
        private FP gripFriction;
        private WheelFrictionBlender gripFrictionBlender = DefaultGripFrictionBlender;
        private FP maxMotorForceDt;
        private FP maximumBackwardForce = FP.MaxValue;
        private FP maximumForwardForce = FP.MaxValue;
        internal SolverSettings solverSettings = new SolverSettings();
        private FP targetSpeed;
        private Wheel wheel;
        internal int numIterationsAtZeroImpulse;
        private Entity vehicleEntity, supportEntity;

        //Inverse effective mass matrix
        internal FP velocityToImpulse;
        private bool supportIsDynamic;

        /// <summary>
        /// Constructs a new wheel motor.
        /// </summary>
        /// <param name="gripFriction">Friction coefficient of the wheel.  Blended with the ground's friction coefficient and normal force to determine a maximum force.</param>
        /// <param name="maximumForwardForce">Maximum force that the wheel motor can apply when driving forward (a target speed greater than zero).</param>
        /// <param name="maximumBackwardForce">Maximum force that the wheel motor can apply when driving backward (a target speed less than zero).</param>
        public WheelDrivingMotor(FP gripFriction, FP maximumForwardForce, FP maximumBackwardForce)
        {
            GripFriction = gripFriction;
            MaximumForwardForce = maximumForwardForce;
            MaximumBackwardForce = maximumBackwardForce;
        }

        internal WheelDrivingMotor(Wheel wheel)
        {
            Wheel = wheel;
        }

        /// <summary>
        /// Gets the coefficient of grip friction between the wheel and support.
        /// This coefficient is the blended result of the supporting entity's friction and the wheel's friction.
        /// </summary>
        public FP BlendedCoefficient
        {
            get { return currentFrictionCoefficient; }
        }

        /// <summary>
        /// Gets the axis along which the driving forces are applied.
        /// </summary>
        public FPVector3 ForceAxis
        {
            get { return forceAxis; }
        }

        /// <summary>
        /// Gets or sets the coefficient of forward-backward gripping friction for this wheel.
        /// This coefficient and the supporting entity's coefficient of friction will be 
        /// taken into account to determine the used coefficient at any given time.
        /// </summary>
        public FP GripFriction
        {
            get { return gripFriction; }
            set { gripFriction = MathHelper.Max(value, F64.C0); }
        }

        /// <summary>
        /// Gets or sets the function used to blend the supporting entity's friction and the wheel's friction.
        /// </summary>
        public WheelFrictionBlender GripFrictionBlender
        {
            get { return gripFrictionBlender; }
            set { gripFrictionBlender = value; }
        }

        /// <summary>
        /// Gets or sets the maximum force that the wheel motor can apply when driving backward (a target speed less than zero).
        /// </summary>
        public FP MaximumBackwardForce
        {
            get { return maximumBackwardForce; }
            set { maximumBackwardForce = value; }
        }

        /// <summary>
        /// Gets or sets the maximum force that the wheel motor can apply when driving forward (a target speed greater than zero).
        /// </summary>
        public FP MaximumForwardForce
        {
            get { return maximumForwardForce; }
            set { maximumForwardForce = value; }
        }

        /// <summary>
        /// Gets or sets the target speed of this wheel.
        /// </summary>
        public FP TargetSpeed
        {
            get { return targetSpeed; }
            set { targetSpeed = value; }
        }

        /// <summary>
        /// Gets the force this wheel's motor is applying.
        /// </summary>
        public FP TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the wheel that this motor applies to.
        /// </summary>
        public Wheel Wheel
        {
            get { return wheel; }
            internal set { wheel = value; }
        }

        #region ISolverSettings Members

        /// <summary>
        /// Gets the solver settings used by this wheel constraint.
        /// </summary>
        public SolverSettings SolverSettings
        {
            get { return solverSettings; }
        }

        #endregion

        /// <summary>
        /// Gets the relative velocity between the ground and wheel.
        /// </summary>
        /// <returns>Relative velocity between the ground and wheel.</returns>
        public FP RelativeVelocity
        {
            get
            {
                FP velocity = F64.C0;
                if (vehicleEntity != null)
                    velocity += vehicleEntity._linearVelocity.x * linearAX + vehicleEntity._linearVelocity.y * linearAY + vehicleEntity._linearVelocity.z * linearAZ +
                                  vehicleEntity._angularVelocity.x * angularAX + vehicleEntity._angularVelocity.y * angularAY + vehicleEntity._angularVelocity.z * angularAZ;
                if (supportEntity != null)
                    velocity += -supportEntity._linearVelocity.x * linearAX - supportEntity._linearVelocity.y * linearAY - supportEntity._linearVelocity.z * linearAZ +
                                supportEntity._angularVelocity.x * angularBX + supportEntity._angularVelocity.y * angularBY + supportEntity._angularVelocity.z * angularBZ;
                return velocity;
            }
        }

        internal FP ApplyImpulse()
        {
            //Compute relative velocity
            FP lambda = (RelativeVelocity
                            - targetSpeed) //Add in the extra goal speed
                           * velocityToImpulse; //convert to impulse


            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse += lambda;
            //Don't brake, and take into account the motor's maximum force.
            if (targetSpeed > F64.C0)
                accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse, F64.C0, maxMotorForceDt); //MathHelper.Min(MathHelper.Max(accumulatedImpulse, 0), myMaxMotorForceDt);
            else if (targetSpeed < F64.C0)
                accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse, maxMotorForceDt, F64.C0); //MathHelper.Max(MathHelper.Min(accumulatedImpulse, 0), myMaxMotorForceDt);
            else
                accumulatedImpulse = F64.C0;
            //Friction
            FP maxForce = currentFrictionCoefficient * wheel.suspension.accumulatedImpulse;
            accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse, maxForce, -maxForce);
            lambda = accumulatedImpulse - previousAccumulatedImpulse;


            //Apply the impulse
#if !WINDOWS
            FPVector3 linear = new FPVector3();
            FPVector3 angular = new FPVector3();
#else
            Vector3 linear, angular;
#endif
            linear.x = lambda * linearAX;
            linear.y = lambda * linearAY;
            linear.z = lambda * linearAZ;
            if (vehicleEntity._isDynamic)
            {
                angular.x = lambda * angularAX;
                angular.y = lambda * angularAY;
                angular.z = lambda * angularAZ;
                vehicleEntity.ApplyLinearImpulse(ref linear);
                vehicleEntity.ApplyAngularImpulse(ref angular);
            }
            if (supportIsDynamic)
            {
                linear.x = -linear.x;
                linear.y = -linear.y;
                linear.z = -linear.z;
                angular.x = lambda * angularBX;
                angular.y = lambda * angularBY;
                angular.z = lambda * angularBZ;
                supportEntity.ApplyLinearImpulse(ref linear);
                supportEntity.ApplyAngularImpulse(ref angular);
            }

            return lambda;
        }

        internal void PreStep(FP dt)
        {
            vehicleEntity = wheel.Vehicle.Body;
            supportEntity = wheel.SupportingEntity;
            supportIsDynamic = supportEntity != null && supportEntity._isDynamic;

            FPVector3.Cross(ref wheel.normal, ref wheel.slidingFriction.slidingFrictionAxis, out forceAxis);
            forceAxis.Normalize();
            //Do not need to check for normalize safety because normal and sliding friction axis must be perpendicular.

            linearAX = forceAxis.x;
            linearAY = forceAxis.y;
            linearAZ = forceAxis.z;

            //angular A = Ra x N
            angularAX = (wheel.ra.y * linearAZ) - (wheel.ra.z * linearAY);
            angularAY = (wheel.ra.z * linearAX) - (wheel.ra.x * linearAZ);
            angularAZ = (wheel.ra.x * linearAY) - (wheel.ra.y * linearAX);

            //Angular B = N x Rb
            angularBX = (linearAY * wheel.rb.z) - (linearAZ * wheel.rb.y);
            angularBY = (linearAZ * wheel.rb.x) - (linearAX * wheel.rb.z);
            angularBZ = (linearAX * wheel.rb.y) - (linearAY * wheel.rb.x);

            //Compute inverse effective mass matrix
            FP entryA, entryB;

            //these are the transformed coordinates
            FP tX, tY, tZ;
            if (vehicleEntity._isDynamic)
            {
                tX = angularAX * vehicleEntity.inertiaTensorInverse.M11 + angularAY * vehicleEntity.inertiaTensorInverse.M21 + angularAZ * vehicleEntity.inertiaTensorInverse.M31;
                tY = angularAX * vehicleEntity.inertiaTensorInverse.M12 + angularAY * vehicleEntity.inertiaTensorInverse.M22 + angularAZ * vehicleEntity.inertiaTensorInverse.M32;
                tZ = angularAX * vehicleEntity.inertiaTensorInverse.M13 + angularAY * vehicleEntity.inertiaTensorInverse.M23 + angularAZ * vehicleEntity.inertiaTensorInverse.M33;
                entryA = tX * angularAX + tY * angularAY + tZ * angularAZ + vehicleEntity.inverseMass;
            }
            else
                entryA = F64.C0;

            if (supportIsDynamic)
            {
                tX = angularBX * supportEntity.inertiaTensorInverse.M11 + angularBY * supportEntity.inertiaTensorInverse.M21 + angularBZ * supportEntity.inertiaTensorInverse.M31;
                tY = angularBX * supportEntity.inertiaTensorInverse.M12 + angularBY * supportEntity.inertiaTensorInverse.M22 + angularBZ * supportEntity.inertiaTensorInverse.M32;
                tZ = angularBX * supportEntity.inertiaTensorInverse.M13 + angularBY * supportEntity.inertiaTensorInverse.M23 + angularBZ * supportEntity.inertiaTensorInverse.M33;
                entryB = tX * angularBX + tY * angularBY + tZ * angularBZ + supportEntity.inverseMass;
            }
            else
                entryB = F64.C0;

            velocityToImpulse = -1 / (entryA + entryB); //Softness?

            currentFrictionCoefficient = gripFrictionBlender(gripFriction, wheel.supportMaterial.kineticFriction, true, wheel);

            //Compute the maximum force
            if (targetSpeed > F64.C0)
                maxMotorForceDt = maximumForwardForce * dt;
            else
                maxMotorForceDt = -maximumBackwardForce * dt;




        }

        internal void ExclusiveUpdate()
        {
            //Warm starting
#if !WINDOWS
            FPVector3 linear = new FPVector3();
            FPVector3 angular = new FPVector3();
#else
            Vector3 linear, angular;
#endif
            linear.x = accumulatedImpulse * linearAX;
            linear.y = accumulatedImpulse * linearAY;
            linear.z = accumulatedImpulse * linearAZ;
            if (vehicleEntity._isDynamic)
            {
                angular.x = accumulatedImpulse * angularAX;
                angular.y = accumulatedImpulse * angularAY;
                angular.z = accumulatedImpulse * angularAZ;
                vehicleEntity.ApplyLinearImpulse(ref linear);
                vehicleEntity.ApplyAngularImpulse(ref angular);
            }
            if (supportIsDynamic)
            {
                linear.x = -linear.x;
                linear.y = -linear.y;
                linear.z = -linear.z;
                angular.x = accumulatedImpulse * angularBX;
                angular.y = accumulatedImpulse * angularBY;
                angular.z = accumulatedImpulse * angularBZ;
                supportEntity.ApplyLinearImpulse(ref linear);
                supportEntity.ApplyAngularImpulse(ref angular);
            }
        }
    }
}