using System;
using BEPUphysics.Entities;
 
using BEPUphysics.Settings;
using BEPUutilities;
using BEPUutilities.DataStructures;
using FixMath.NET;

namespace BEPUphysics.Constraints.Collision
{
    /// <summary>
    /// Computes the forces necessary to slow down and stop twisting motion in a collision between two entities.
    /// </summary>
    public class TwistFrictionConstraint : SolverUpdateable
    {
        private readonly FP[] leverArms = new FP[4];
        private ConvexContactManifoldConstraint contactManifoldConstraint;
        ///<summary>
        /// Gets the contact manifold constraint that owns this constraint.
        ///</summary>
        public ConvexContactManifoldConstraint ContactManifoldConstraint { get { return contactManifoldConstraint; } }
        internal FP accumulatedImpulse;
        private FP angularX, angularY, angularZ;
        private int contactCount;
        private FP friction;
        Entity entityA, entityB;
        bool entityADynamic, entityBDynamic;
        private FP velocityToImpulse;

        ///<summary>
        /// Constructs a new twist friction constraint.
        ///</summary>
        public TwistFrictionConstraint()
        {
            isActive = false;
        }

        /// <summary>
        /// Gets the torque applied by twist friction.
        /// </summary>
        public FP TotalTorque
        {
            get { return accumulatedImpulse; }
        }

        ///<summary>
        /// Gets the angular velocity between the associated entities.
        ///</summary>
        public FP RelativeVelocity
        {
            get
            {
                FP lambda = F64.C0;
                if (entityA != null)
                    lambda = entityA._angularVelocity.x * angularX + entityA._angularVelocity.y * angularY + entityA._angularVelocity.z * angularZ;
                if (entityB != null)
                    lambda -= entityB._angularVelocity.x * angularX + entityB._angularVelocity.y * angularY + entityB._angularVelocity.z * angularZ;
                return lambda;
            }
        }

        /// <summary>
        /// Computes one iteration of the constraint to meet the solver updateable's goal.
        /// </summary>
        /// <returns>The rough applied impulse magnitude.</returns>
        public override FP SolveIteration()
        {
            //Compute relative velocity.  Collisions can occur between an entity and a non-entity.  If it's not an entity, assume it's not moving.
            FP lambda = RelativeVelocity;
            
            lambda *= velocityToImpulse; //convert to impulse

            //Clamp accumulated impulse
            FP previousAccumulatedImpulse = accumulatedImpulse;
            FP maximumFrictionForce = F64.C0;
            for (int i = 0; i < contactCount; i++)
            {
                maximumFrictionForce += leverArms[i] * contactManifoldConstraint.penetrationConstraints.Elements[i].accumulatedImpulse;
            }
            maximumFrictionForce *= friction;
            accumulatedImpulse = MathHelper.Clamp(accumulatedImpulse + lambda, -maximumFrictionForce, maximumFrictionForce); //instead of maximumFrictionForce, could recompute each iteration...
            lambda = accumulatedImpulse - previousAccumulatedImpulse;


            //Apply the impulse
#if !WINDOWS
            FPVector3 angular = new FPVector3();
#else
            Vector3 angular;
#endif
            angular.x = lambda * angularX;
            angular.y = lambda * angularY;
            angular.z = lambda * angularZ;
            if (entityADynamic)
            {
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBDynamic)
            {
                angular.x = -angular.x;
                angular.y = -angular.y;
                angular.z = -angular.z;
                entityB.ApplyAngularImpulse(ref angular);
            }


            return FP.Abs(lambda);
        }


        ///<summary>
        /// Performs the frame's configuration step.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void Update(FP dt)
        {

            entityADynamic = entityA != null && entityA._isDynamic;
            entityBDynamic = entityB != null && entityB._isDynamic;

            //Compute the jacobian......  Real hard!
            FPVector3 normal = contactManifoldConstraint.penetrationConstraints.Elements[0].contact.Normal;
            angularX = normal.x;
            angularY = normal.y;
            angularZ = normal.z;

            //Compute inverse effective mass matrix
            FP entryA, entryB;

            //these are the transformed coordinates
            FP tX, tY, tZ;
            if (entityADynamic)
            {
                tX = angularX * entityA.inertiaTensorInverse.M11 + angularY * entityA.inertiaTensorInverse.M21 + angularZ * entityA.inertiaTensorInverse.M31;
                tY = angularX * entityA.inertiaTensorInverse.M12 + angularY * entityA.inertiaTensorInverse.M22 + angularZ * entityA.inertiaTensorInverse.M32;
                tZ = angularX * entityA.inertiaTensorInverse.M13 + angularY * entityA.inertiaTensorInverse.M23 + angularZ * entityA.inertiaTensorInverse.M33;
                entryA = tX * angularX + tY * angularY + tZ * angularZ + entityA.inverseMass;
            }
            else
                entryA = F64.C0;

            if (entityBDynamic)
            {
                tX = angularX * entityB.inertiaTensorInverse.M11 + angularY * entityB.inertiaTensorInverse.M21 + angularZ * entityB.inertiaTensorInverse.M31;
                tY = angularX * entityB.inertiaTensorInverse.M12 + angularY * entityB.inertiaTensorInverse.M22 + angularZ * entityB.inertiaTensorInverse.M32;
                tZ = angularX * entityB.inertiaTensorInverse.M13 + angularY * entityB.inertiaTensorInverse.M23 + angularZ * entityB.inertiaTensorInverse.M33;
                entryB = tX * angularX + tY * angularY + tZ * angularZ + entityB.inverseMass;
            }
            else
                entryB = F64.C0;

            velocityToImpulse = -1 / (entryA + entryB);


            //Compute the relative velocity to determine what kind of friction to use
            FP relativeAngularVelocity = RelativeVelocity;
            //Set up friction and find maximum friction force
            FPVector3 relativeSlidingVelocity = contactManifoldConstraint.SlidingFriction.relativeVelocity;
            friction = FP.Abs(relativeAngularVelocity) > CollisionResponseSettings.StaticFrictionVelocityThreshold ||
					   FP.Abs(relativeSlidingVelocity.x) + FP.Abs(relativeSlidingVelocity.y) + FP.Abs(relativeSlidingVelocity.z) > CollisionResponseSettings.StaticFrictionVelocityThreshold
                           ? contactManifoldConstraint.materialInteraction.KineticFriction
                           : contactManifoldConstraint.materialInteraction.StaticFriction;
            friction *= CollisionResponseSettings.TwistFrictionFactor;

            contactCount = contactManifoldConstraint.penetrationConstraints.Count;

            FPVector3 contactOffset;
            for (int i = 0; i < contactCount; i++)
            {
                FPVector3.Subtract(ref contactManifoldConstraint.penetrationConstraints.Elements[i].contact.Position, ref contactManifoldConstraint.SlidingFriction.manifoldCenter, out contactOffset);
                leverArms[i] = contactOffset.Length();
            }



        }

        /// <summary>
        /// Performs any pre-solve iteration work that needs exclusive
        /// access to the members of the solver updateable.
        /// Usually, this is used for applying warmstarting impulses.
        /// </summary>
        public override void ExclusiveUpdate()
        {
            //Apply the warmstarting impulse.
#if !WINDOWS
            FPVector3 angular = new FPVector3();
#else
            Vector3 angular;
#endif
            angular.x = accumulatedImpulse * angularX;
            angular.y = accumulatedImpulse * angularY;
            angular.z = accumulatedImpulse * angularZ;
            if (entityADynamic)
            {
                entityA.ApplyAngularImpulse(ref angular);
            }
            if (entityBDynamic)
            {
                angular.x = -angular.x;
                angular.y = -angular.y;
                angular.z = -angular.z;
                entityB.ApplyAngularImpulse(ref angular);
            }
        }

        internal void Setup(ConvexContactManifoldConstraint contactManifoldConstraint)
        {
            this.contactManifoldConstraint = contactManifoldConstraint;
            isActive = true;

            entityA = contactManifoldConstraint.EntityA;
            entityB = contactManifoldConstraint.EntityB;
        }

        internal void CleanUp()
        {
            accumulatedImpulse = F64.C0;
            contactManifoldConstraint = null;
            entityA = null;
            entityB = null;
            isActive = false;
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