using System;
using BEPUphysics.Entities;
 
using BEPUutilities.DataStructures;
using BEPUphysics.Settings;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints.Collision
{
    /// <summary>
    /// Computes the friction force for a contact when central friction cannot be used.
    /// </summary>
    public class ContactFrictionConstraint : SolverUpdateable
    {
        private ContactManifoldConstraint contactManifoldConstraint;
        ///<summary>
        /// Gets the manifold constraint associated with this friction constraint.
        ///</summary>
        public ContactManifoldConstraint ContactManifoldConstraint
        {
            get
            {
                return contactManifoldConstraint;
            }
        }
        private ContactPenetrationConstraint penetrationConstraint;
        ///<summary>
        /// Gets the penetration constraint associated with this friction constraint.
        ///</summary>
        public ContactPenetrationConstraint PenetrationConstraint
        {
            get
            {
                return penetrationConstraint;
            }
        }

        ///<summary>
        /// Constructs a new friction constraint.
        ///</summary>
        public ContactFrictionConstraint()
        {
            isActive = false;
        }

        internal FP accumulatedImpulse;
        //Fix64 linearBX, linearBY, linearBZ;
        private FP angularAX, angularAY, angularAZ;
        private FP angularBX, angularBY, angularBZ;

        //Inverse effective mass matrix


        private FP friction;
        internal FP linearAX, linearAY, linearAZ;
        private Entity entityA, entityB;
        private bool entityAIsDynamic, entityBIsDynamic;
        private FP velocityToImpulse;


        ///<summary>
        /// Configures the friction constraint for a new contact.
        ///</summary>
        ///<param name="contactManifoldConstraint">Manifold to which the constraint belongs.</param>
        ///<param name="penetrationConstraint">Penetration constraint associated with this friction constraint.</param>
        public void Setup(ContactManifoldConstraint contactManifoldConstraint, ContactPenetrationConstraint penetrationConstraint)
        {
            this.contactManifoldConstraint = contactManifoldConstraint;
            this.penetrationConstraint = penetrationConstraint;
            IsActive = true;
            linearAX = F64.C0;
            linearAY = F64.C0;
            linearAZ = F64.C0;

            entityA = contactManifoldConstraint.EntityA;
            entityB = contactManifoldConstraint.EntityB;
        }

        ///<summary>
        /// Cleans upt he friction constraint.
        ///</summary>
        public void CleanUp()
        {
            accumulatedImpulse = F64.C0;
            contactManifoldConstraint = null;
            penetrationConstraint = null;
            entityA = null;
            entityB = null;
            IsActive = false;
        }

        /// <summary>
        /// Gets the direction in which the friction force acts.
        /// </summary>
        public FPVector3 FrictionDirection
        {
            get { return new FPVector3(linearAX, linearAY, linearAZ); }
        }

        /// <summary>
        /// Gets the total impulse applied by this friction constraint in the last time step.
        /// </summary>
        public FP TotalImpulse
        {
            get { return accumulatedImpulse; }
        }

        ///<summary>
        /// Gets the relative velocity of the constraint.  This is the velocity along the tangent movement direction.
        ///</summary>
        public FP RelativeVelocity
        {
            get
            {
                FP velocity = F64.C0;
                if (entityA != null)
                    velocity += entityA._linearVelocity.x * linearAX + entityA._linearVelocity.y * linearAY + entityA._linearVelocity.z * linearAZ +
                                entityA._angularVelocity.x * angularAX + entityA._angularVelocity.y * angularAY + entityA._angularVelocity.z * angularAZ;
                if (entityB != null)
                    velocity += -entityB._linearVelocity.x * linearAX - entityB._linearVelocity.y * linearAY - entityB._linearVelocity.z * linearAZ +
                                entityB._angularVelocity.x * angularBX + entityB._angularVelocity.y * angularBY + entityB._angularVelocity.z * angularBZ;
                return velocity;
            }
        }


        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            //Compute relative velocity and convert to impulse
            FP lambda = RelativeVelocity * velocityToImpulse;


            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            FP maxForce = friction * penetrationConstraint.accumulatedImpulse;
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
            if (entityAIsDynamic)
            {
                angular.x = lambda * angularAX;
                angular.y = lambda * angularAY;
                angular.z = lambda * angularAZ;
                entityA.ApplyLinearImpulse(ref linear);
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBIsDynamic)
            {
                linear.x = -linear.x;
                linear.y = -linear.y;
                linear.z = -linear.z;
                angular.x = lambda * angularBX;
                angular.y = lambda * angularBY;
                angular.z = lambda * angularBZ;
                entityB.ApplyLinearImpulse(ref linear);
                entityB.ApplyAngularImpulse(ref angular);
            }

            return FP.Abs(lambda);
        }

        /// <summary>
        /// Initializes the constraint for this frame.
        /// </summary>
        /// <param name="dt">Time since the last frame.</param>
        public override void Update(FP dt)
        {


            entityAIsDynamic = entityA != null && entityA._isDynamic;
            entityBIsDynamic = entityB != null && entityB._isDynamic;

            //Compute the three dimensional relative velocity at the point.

            FPVector3 velocityA = new FPVector3(), velocityB = new FPVector3();
            FPVector3 ra = penetrationConstraint.ra, rb = penetrationConstraint.rb;
            if (entityA != null)
            {
                FPVector3.Cross(ref entityA._angularVelocity, ref ra, out velocityA);
                FPVector3.Add(ref velocityA, ref entityA._linearVelocity, out velocityA);
            }
            if (entityB != null)
            {
                FPVector3.Cross(ref entityB._angularVelocity, ref rb, out velocityB);
                FPVector3.Add(ref velocityB, ref entityB._linearVelocity, out velocityB);
            }
            FPVector3 relativeVelocity;
            FPVector3.Subtract(ref velocityA, ref velocityB, out relativeVelocity);

            //Get rid of the normal velocity.
            FPVector3 normal = penetrationConstraint.contact.Normal;
            FP normalVelocityScalar = normal.x * relativeVelocity.x + normal.y * relativeVelocity.y + normal.z * relativeVelocity.z;
            relativeVelocity.x -= normalVelocityScalar * normal.x;
            relativeVelocity.y -= normalVelocityScalar * normal.y;
            relativeVelocity.z -= normalVelocityScalar * normal.z;

            //Create the jacobian entry and decide the friction coefficient.
            FP length = relativeVelocity.LengthSquared();
            if (length > Toolbox.Epsilon)
            {
                length = FP.Sqrt(length);
                linearAX = relativeVelocity.x / length;
                linearAY = relativeVelocity.y / length;
                linearAZ = relativeVelocity.z / length;

                friction = length > CollisionResponseSettings.StaticFrictionVelocityThreshold
                               ? contactManifoldConstraint.materialInteraction.KineticFriction
                               : contactManifoldConstraint.materialInteraction.StaticFriction;
            }
            else
            {
                //If there's no velocity, there's no jacobian.  Give up.
                //This is 'fast' in that it will early out on essentially resting objects,
                //but it may introduce instability.
                //If it doesn't look good, try the next approach.
                //isActive = false;
                //return;

                //if the above doesn't work well, try using the previous frame's jacobian.
                if (linearAX != F64.C0 || linearAY != F64.C0 || linearAZ != F64.C0)
                {
                    friction = contactManifoldConstraint.materialInteraction.StaticFriction;
                }
                else
                {
                    //Can't really do anything here, give up.
                    isActiveInSolver = false;
                    return;
                    //Could also cross the up with normal to get a random direction.  Questionable value.
                }
            }


            //angular A = Ra x N
            angularAX = (ra.y * linearAZ) - (ra.z * linearAY);
            angularAY = (ra.z * linearAX) - (ra.x * linearAZ);
            angularAZ = (ra.x * linearAY) - (ra.y * linearAX);

            //Angular B = N x Rb
            angularBX = (linearAY * rb.z) - (linearAZ * rb.y);
            angularBY = (linearAZ * rb.x) - (linearAX * rb.z);
            angularBZ = (linearAX * rb.y) - (linearAY * rb.x);

            //Compute inverse effective mass matrix
            FP entryA, entryB;

            //these are the transformed coordinates
            FP tX, tY, tZ;
            if (entityAIsDynamic)
            {
                tX = angularAX * entityA.inertiaTensorInverse.M11 + angularAY * entityA.inertiaTensorInverse.M21 + angularAZ * entityA.inertiaTensorInverse.M31;
                tY = angularAX * entityA.inertiaTensorInverse.M12 + angularAY * entityA.inertiaTensorInverse.M22 + angularAZ * entityA.inertiaTensorInverse.M32;
                tZ = angularAX * entityA.inertiaTensorInverse.M13 + angularAY * entityA.inertiaTensorInverse.M23 + angularAZ * entityA.inertiaTensorInverse.M33;
                entryA = tX * angularAX + tY * angularAY + tZ * angularAZ + entityA.inverseMass;
            }
            else
                entryA = F64.C0;

            if (entityBIsDynamic)
            {
                tX = angularBX * entityB.inertiaTensorInverse.M11 + angularBY * entityB.inertiaTensorInverse.M21 + angularBZ * entityB.inertiaTensorInverse.M31;
                tY = angularBX * entityB.inertiaTensorInverse.M12 + angularBY * entityB.inertiaTensorInverse.M22 + angularBZ * entityB.inertiaTensorInverse.M32;
                tZ = angularBX * entityB.inertiaTensorInverse.M13 + angularBY * entityB.inertiaTensorInverse.M23 + angularBZ * entityB.inertiaTensorInverse.M33;
                entryB = tX * angularBX + tY * angularBY + tZ * angularBZ + entityB.inverseMass;
            }
            else
                entryB = F64.C0;

            velocityToImpulse = -1 / (entryA + entryB); //Softness?



        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
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
            if (entityAIsDynamic)
            {
                angular.x = accumulatedImpulse * angularAX;
                angular.y = accumulatedImpulse * angularAY;
                angular.z = accumulatedImpulse * angularAZ;
                entityA.ApplyLinearImpulse(ref linear);
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBIsDynamic)
            {
                linear.x = -linear.x;
                linear.y = -linear.y;
                linear.z = -linear.z;
                angular.x = accumulatedImpulse * angularBX;
                angular.y = accumulatedImpulse * angularBY;
                angular.z = accumulatedImpulse * angularBZ;
                entityB.ApplyLinearImpulse(ref linear);
                entityB.ApplyAngularImpulse(ref angular);
            }
        }

        protected internal override void CollectInvolvedEntities(RawList<Entity> outputInvolvedEntities)
        {
            //This should never really have to be called.
            if (entityA != null)
                outputInvolvedEntities.Add(entityA);
            if (entityB != null)
                outputInvolvedEntities.Add(entityB);
        }
    }
}