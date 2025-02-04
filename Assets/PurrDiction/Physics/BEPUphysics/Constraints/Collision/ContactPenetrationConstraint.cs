using BEPUphysics.Entities;
using BEPUphysics.CollisionTests;
using BEPUphysics.Settings;
using BEPUutilities;
using System;
using BEPUutilities.DataStructures;
using FixMath.NET;

namespace BEPUphysics.Constraints.Collision
{
    /// <summary>
    /// Computes the forces necessary to keep two entities from going through each other at a contact point.
    /// </summary>
    public class ContactPenetrationConstraint : SolverUpdateable
    {
        internal Contact contact;

        ///<summary>
        /// Gets the contact associated with this penetration constraint.
        ///</summary>
        public Contact Contact { get { return contact; } }
        internal FP accumulatedImpulse;
        //Fix64 linearBX, linearBY, linearBZ;
        internal FP angularAX, angularAY, angularAZ;
        internal FP angularBX, angularBY, angularBZ;

        private FP softness;
        private FP bias;
        private FP linearAX, linearAY, linearAZ;
        private Entity entityA, entityB;
        private bool entityADynamic, entityBDynamic;
        //Inverse effective mass matrix
        internal FP velocityToImpulse;
        private ContactManifoldConstraint contactManifoldConstraint;

        internal FPVector3 ra, rb;

        ///<summary>
        /// Constructs a new penetration constraint.
        ///</summary>
        public ContactPenetrationConstraint()
        {
            isActive = false;
        }


        ///<summary>
        /// Configures the penetration constraint.
        ///</summary>
        ///<param name="contactManifoldConstraint">Owning manifold constraint.</param>
        ///<param name="contact">Contact associated with the penetration constraint.</param>
        public void Setup(ContactManifoldConstraint contactManifoldConstraint, Contact contact)
        {
            this.contactManifoldConstraint = contactManifoldConstraint;
            this.contact = contact;
            isActive = true;

            entityA = contactManifoldConstraint.EntityA;
            entityB = contactManifoldConstraint.EntityB;

        }

        ///<summary>
        /// Cleans up the constraint.
        ///</summary>
        public void CleanUp()
        {
            accumulatedImpulse = F64.C0;
            contactManifoldConstraint = null;
            contact = null;
            entityA = null;
            entityB = null;
            isActive = false;


        }

        /// <summary>
        /// Gets the total normal impulse applied by this penetration constraint to maintain the separation of the involved entities.
        /// </summary>
        public FP NormalImpulse
        {
            get { return accumulatedImpulse; }
        }

        ///<summary>
        /// Gets the relative velocity between the associated entities at the contact point along the contact normal.
        ///</summary>
        public FP RelativeVelocity
        {
            get
            {
                FP lambda = F64.C0;
                if (entityA != null)
                {
                    lambda = entityA.linearVelocity.x * linearAX + entityA.linearVelocity.y * linearAY + entityA.linearVelocity.z * linearAZ +
                             entityA.angularVelocity.x * angularAX + entityA.angularVelocity.y * angularAY + entityA.angularVelocity.z * angularAZ;
                }
                if (entityB != null)
                {
                    lambda += -entityB.linearVelocity.x * linearAX - entityB.linearVelocity.y * linearAY - entityB.linearVelocity.z * linearAZ +
                              entityB.angularVelocity.x * angularBX + entityB.angularVelocity.y * angularBY + entityB.angularVelocity.z * angularBZ;
                }
                return lambda;
            }
        }




        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {

            entityADynamic = entityA != null && entityA.isDynamic;
            entityBDynamic = entityB != null && entityB.isDynamic;

            //Set up the jacobians.
            linearAX = -contact.Normal.x;
            linearAY = -contact.Normal.y;
            linearAZ = -contact.Normal.z;
            //linearBX = -linearAX;
            //linearBY = -linearAY;
            //linearBZ = -linearAZ;



            //angular A = Ra x N
            if (entityA != null)
            {
                FPVector3.Subtract(ref contact.Position, ref entityA.position, out ra);
                angularAX = (ra.y * linearAZ) - (ra.z * linearAY);
                angularAY = (ra.z * linearAX) - (ra.x * linearAZ);
                angularAZ = (ra.x * linearAY) - (ra.y * linearAX);
            }


            //Angular B = N x Rb
            if (entityB != null)
            {
                FPVector3.Subtract(ref contact.Position, ref entityB.position, out rb);
                angularBX = (linearAY * rb.z) - (linearAZ * rb.y);
                angularBY = (linearAZ * rb.x) - (linearAX * rb.z);
                angularBZ = (linearAX * rb.y) - (linearAY * rb.x);
            }


            //Compute inverse effective mass matrix
            FP entryA, entryB;

            //these are the transformed coordinates
            FP tX, tY, tZ;
            if (entityADynamic)
            {
                tX = angularAX * entityA.inertiaTensorInverse.M11 + angularAY * entityA.inertiaTensorInverse.M21 + angularAZ * entityA.inertiaTensorInverse.M31;
                tY = angularAX * entityA.inertiaTensorInverse.M12 + angularAY * entityA.inertiaTensorInverse.M22 + angularAZ * entityA.inertiaTensorInverse.M32;
                tZ = angularAX * entityA.inertiaTensorInverse.M13 + angularAY * entityA.inertiaTensorInverse.M23 + angularAZ * entityA.inertiaTensorInverse.M33;
                entryA = tX * angularAX + tY * angularAY + tZ * angularAZ + entityA.inverseMass;
            }
            else
                entryA = F64.C0;

            if (entityBDynamic)
            {
                tX = angularBX * entityB.inertiaTensorInverse.M11 + angularBY * entityB.inertiaTensorInverse.M21 + angularBZ * entityB.inertiaTensorInverse.M31;
                tY = angularBX * entityB.inertiaTensorInverse.M12 + angularBY * entityB.inertiaTensorInverse.M22 + angularBZ * entityB.inertiaTensorInverse.M32;
                tZ = angularBX * entityB.inertiaTensorInverse.M13 + angularBY * entityB.inertiaTensorInverse.M23 + angularBZ * entityB.inertiaTensorInverse.M33;
                entryB = tX * angularBX + tY * angularBY + tZ * angularBZ + entityB.inverseMass;
            }
            else
                entryB = F64.C0;

            //If we used a single fixed softness value, then heavier objects will tend to 'squish' more than light objects.
            //In the extreme case, very heavy objects could simply fall through the ground by force of gravity.
            //To see why this is the case, consider that a given dt, softness, and bias factor correspond to an equivalent spring's damping and stiffness coefficients.
            //Imagine trying to hang objects of different masses on the fixed-strength spring: obviously, heavier ones will pull it further down.

            //To counteract this, scale the softness value based on the effective mass felt by the constraint.
            //Larger effective masses should correspond to smaller softnesses so that the spring has the same positional behavior.
            //Fortunately, we're already computing the necessary values: the raw, unsoftened effective mass inverse shall be used to compute the softness.

            FP effectiveMassInverse = entryA + entryB;
            FP updateRate = F64.C1 / dt;
            softness = CollisionResponseSettings.Softness * effectiveMassInverse * updateRate;
            velocityToImpulse = -1 / (softness + effectiveMassInverse);


            //Bounciness and bias (penetration correction)
            if (contact.PenetrationDepth >= F64.C0)
            {
                bias = MathHelper.Min(
                    MathHelper.Max(F64.C0, contact.PenetrationDepth - CollisionDetectionSettings.AllowedPenetration) *
                    CollisionResponseSettings.PenetrationRecoveryStiffness * updateRate,
                    CollisionResponseSettings.MaximumPenetrationRecoverySpeed);

                if (contactManifoldConstraint.materialInteraction.Bounciness > F64.C0)
                {
                    //Target a velocity which includes a portion of the incident velocity.
                    FP bounceVelocity = -RelativeVelocity;
                    if (bounceVelocity > F64.C0)
                    {
                        var lowThreshold = CollisionResponseSettings.BouncinessVelocityThreshold * F64.C0p3;
                        var velocityFraction = MathHelper.Clamp((bounceVelocity - lowThreshold) / (CollisionResponseSettings.BouncinessVelocityThreshold - lowThreshold + Toolbox.Epsilon), F64.C0, F64.C1);
                        var bouncinessVelocity = velocityFraction * bounceVelocity * contactManifoldConstraint.materialInteraction.Bounciness;
                        bias = MathHelper.Max(bouncinessVelocity, bias);
                    }
                }
            }
            else
            {
                //The contact is actually separated right now.  Allow the solver to target a position that is just barely in collision.
                //If the solver finds that an accumulated negative impulse is required to hit this target, then no work will be done.
                bias = contact.PenetrationDepth * updateRate;

                //This implementation is going to ignore bounciness for now.
                //Since it's not being used for CCD, these negative-depth contacts
                //only really occur in situations where no bounce should occur.

                //if (contactManifoldConstraint.materialInteraction.Bounciness > 0)
                //{
                //    //Target a velocity which includes a portion of the incident velocity.
                //    //The contact isn't colliding currently, but go ahead and target the post-bounce velocity.
                //    //The bias is added to the bounce velocity to simulate the object continuing to the surface and then bouncing off.
                //    Fix64 relativeVelocity = -RelativeVelocity;
                //    if (relativeVelocity > CollisionResponseSettings.BouncinessVelocityThreshold)
                //        bias = relativeVelocity * contactManifoldConstraint.materialInteraction.Bounciness + bias;
                //}
            }


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
            if (entityADynamic)
            {
                angular.x = accumulatedImpulse * angularAX;
                angular.y = accumulatedImpulse * angularAY;
                angular.z = accumulatedImpulse * angularAZ;
                entityA.ApplyLinearImpulse(ref linear);
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBDynamic)
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


        /// <summary>
        /// Computes and applies an impulse to keep the colliders from penetrating.
        /// </summary>
        /// <returns>Impulse applied.</returns>
        public override FP SolveIteration()
        {

            //Compute relative velocity
            FP lambda = (RelativeVelocity - bias + softness * accumulatedImpulse) * velocityToImpulse;

            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            accumulatedImpulse = MathHelper.Max(F64.C0, accumulatedImpulse + lambda);
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
            if (entityADynamic)
            {
                angular.x = lambda * angularAX;
                angular.y = lambda * angularAY;
                angular.z = lambda * angularAZ;
                entityA.ApplyLinearImpulse(ref linear);
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBDynamic)
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