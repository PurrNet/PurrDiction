using System;
using BEPUphysics.Constraints;
using BEPUphysics.Entities;
 
using BEPUphysics.Materials;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Vehicle
{
    /// <summary>
    /// Attempts to resist sliding motion of a vehicle.
    /// </summary>
    public class WheelSlidingFriction : ISolverSettings
    {
        #region Static Stuff

        /// <summary>
        /// Default blender used by WheelSlidingFriction constraints.
        /// </summary>
        public static WheelFrictionBlender DefaultSlidingFrictionBlender;

        static WheelSlidingFriction()
        {
            DefaultSlidingFrictionBlender = BlendFriction;
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
        private FP angularAX, angularAY, angularAZ;
        private FP angularBX, angularBY, angularBZ;
        internal bool isActive = true;
        private FP linearAX, linearAY, linearAZ;
        private FP blendedCoefficient;
        private FP kineticCoefficient;
        private WheelFrictionBlender frictionBlender = DefaultSlidingFrictionBlender;
        internal FPVector3 slidingFrictionAxis;
        internal SolverSettings solverSettings = new SolverSettings();
        private FP staticCoefficient;
        private FP staticFrictionVelocityThreshold = F64.C5;
        private Wheel wheel;
        internal int numIterationsAtZeroImpulse;
        private Entity vehicleEntity, supportEntity;

        //Inverse effective mass matrix
        private FP velocityToImpulse;

        /// <summary>
        /// Constructs a new sliding friction object for a wheel.
        /// </summary>
        /// <param name="dynamicCoefficient">Coefficient of dynamic sliding friction to be blended with the supporting entity's friction.</param>
        /// <param name="staticCoefficient">Coefficient of static sliding friction to be blended with the supporting entity's friction.</param>
        public WheelSlidingFriction(FP dynamicCoefficient, FP staticCoefficient)
        {
            KineticCoefficient = dynamicCoefficient;
            StaticCoefficient = staticCoefficient;
        }

        internal WheelSlidingFriction(Wheel wheel)
        {
            Wheel = wheel;
        }

        /// <summary>
        /// Gets the coefficient of sliding friction between the wheel and support.
        /// This coefficient is the blended result of the supporting entity's friction and the wheel's friction.
        /// </summary>
        public FP BlendedCoefficient
        {
            get { return blendedCoefficient; }
        }

        /// <summary>
        /// Gets or sets the coefficient of dynamic horizontal sliding friction for this wheel.
        /// This coefficient and the supporting entity's coefficient of friction will be 
        /// taken into account to determine the used coefficient at any given time.
        /// </summary>
        public FP KineticCoefficient
        {
            get { return kineticCoefficient; }
            set { kineticCoefficient = MathHelper.Max(value, F64.C0); }
        }

        /// <summary>
        /// Gets or sets the function used to blend the supporting entity's friction and the wheel's friction.
        /// </summary>
        public WheelFrictionBlender FrictionBlender
        {
            get { return frictionBlender; }
            set { frictionBlender = value; }
        }

        /// <summary>
        /// Gets the axis along which sliding friction is applied.
        /// </summary>
        public FPVector3 SlidingFrictionAxis
        {
            get { return slidingFrictionAxis; }
        }

        /// <summary>
        /// Gets or sets the coefficient of static horizontal sliding friction for this wheel.
        /// This coefficient and the supporting entity's coefficient of friction will be 
        /// taken into account to determine the used coefficient at any given time.
        /// </summary>
        public FP StaticCoefficient
        {
            get { return staticCoefficient; }
            set { staticCoefficient = MathHelper.Max(value, F64.C0); }
        }

        /// <summary>
        /// Gets or sets the velocity under which the coefficient of static friction will be used instead of the dynamic one.
        /// </summary>
        public FP StaticFrictionVelocityThreshold
        {
            get { return staticFrictionVelocityThreshold; }
            set { staticFrictionVelocityThreshold = FP.Abs(value); }
        }

        /// <summary>
        /// Gets the force 
        /// </summary>
        public FP TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        /// <summary>
        /// Gets the wheel that this sliding friction applies to.
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

        bool supportIsDynamic;

        ///<summary>
        /// Gets the relative velocity along the sliding direction at the wheel contact.
        ///</summary>
        public FP RelativeVelocity
        {
            get
            {
                FP velocity = vehicleEntity.linearVelocity.x * linearAX + vehicleEntity.linearVelocity.y * linearAY + vehicleEntity.linearVelocity.z * linearAZ +
                            vehicleEntity.angularVelocity.x * angularAX + vehicleEntity.angularVelocity.y * angularAY + vehicleEntity.angularVelocity.z * angularAZ;
                if (supportEntity != null)
                    velocity += -supportEntity.linearVelocity.x * linearAX - supportEntity.linearVelocity.y * linearAY - supportEntity.linearVelocity.z * linearAZ +
                                supportEntity.angularVelocity.x * angularBX + supportEntity.angularVelocity.y * angularBY + supportEntity.angularVelocity.z * angularBZ;
                return velocity;
            }
        }

        internal FP ApplyImpulse()
        {
            //Compute relative velocity and convert to an impulse
            FP lambda = RelativeVelocity * velocityToImpulse;


            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            FP maxForce = -blendedCoefficient * wheel.suspension.accumulatedImpulse;
            accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse + lambda, -maxForce, maxForce);
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
            if (vehicleEntity.isDynamic)
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
            supportIsDynamic = supportEntity != null && supportEntity.isDynamic;
            FPVector3.Cross(ref wheel.worldForwardDirection, ref wheel.normal, out slidingFrictionAxis);
            FP axisLength = slidingFrictionAxis.LengthSquared();
            //Safety against bad cross product
            if (axisLength < Toolbox.BigEpsilon)
            {
                FPVector3.Cross(ref wheel.worldForwardDirection, ref Toolbox.UpVector, out slidingFrictionAxis);
                axisLength = slidingFrictionAxis.LengthSquared();
                if (axisLength < Toolbox.BigEpsilon)
                {
                    FPVector3.Cross(ref wheel.worldForwardDirection, ref Toolbox.RightVector, out slidingFrictionAxis);
                }
            }
            slidingFrictionAxis.Normalize();

            linearAX = slidingFrictionAxis.x;
            linearAY = slidingFrictionAxis.y;
            linearAZ = slidingFrictionAxis.z;

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
            if (vehicleEntity.isDynamic)
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

            //Compute friction.
            //Which coefficient? Check velocity.
            if (FP.Abs(RelativeVelocity) < staticFrictionVelocityThreshold)
                blendedCoefficient = frictionBlender(staticCoefficient, wheel.supportMaterial.staticFriction, false, wheel);
            else
                blendedCoefficient = frictionBlender(kineticCoefficient, wheel.supportMaterial.kineticFriction, true, wheel);



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
            if (vehicleEntity.isDynamic)
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