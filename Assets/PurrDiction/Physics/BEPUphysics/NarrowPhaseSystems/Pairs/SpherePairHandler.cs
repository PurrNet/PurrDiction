using System;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.BroadPhaseSystems;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.CollisionTests;
using BEPUphysics.CollisionTests.CollisionAlgorithms.GJK;
using BEPUphysics.Constraints.Collision;
using BEPUphysics.PositionUpdating;
using BEPUphysics.Settings;

using BEPUphysics.CollisionTests.CollisionAlgorithms;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.CollisionTests.Manifolds;
using BEPUutilities;

namespace BEPUphysics.NarrowPhaseSystems.Pairs
{
    ///<summary>
    /// Handles a sphere-sphere collision pair.
    ///</summary>
    public class SpherePairHandler : ConvexPairHandler
    {
        ConvexCollidable<SphereShape> sphereA;
        ConvexCollidable<SphereShape> sphereB;

        //Using a non-convex one since they have slightly lower overhead than their Convex friends when dealing with a single contact point.
        SphereContactManifold contactManifold = new SphereContactManifold();
        private NonConvexContactManifoldConstraint contactConstraint;

        public override Collidable CollidableA
        {
            get { return sphereA; }
        }
        public override Collidable CollidableB
        {
            get { return sphereB; }
        }
        public override Entities.Entity EntityA
        {
            get { return sphereA.entity; }
        }
        public override Entities.Entity EntityB
        {
            get { return sphereB.entity; }
        }
        /// <summary>
        /// Gets the contact constraint used by the pair handler.
        /// </summary>
        public override ContactManifoldConstraint ContactConstraint
        {
            get
            {
                return contactConstraint;
            }
        }
        /// <summary>
        /// Gets the contact manifold used by the pair handler.
        /// </summary>
        public override ContactManifold ContactManifold
        {
            get { return contactManifold; }
        }

        public SpherePairHandler()
        {
            contactConstraint = new NonConvexContactManifoldConstraint(this);
        }

        ///<summary>
        /// Initializes the pair handler.
        ///</summary>
        ///<param name="entryA">First entry in the pair.</param>
        ///<param name="entryB">Second entry in the pair.</param>
        public override void Initialize(BroadPhaseEntry entryA, BroadPhaseEntry entryB)
        {


            sphereA = entryA as ConvexCollidable<SphereShape>;
            sphereB = entryB as ConvexCollidable<SphereShape>;

            if (sphereA == null || sphereB == null)
            {
                throw new ArgumentException("Inappropriate types used to initialize pair.");
            }

            base.Initialize(entryA, entryB);

        }


        ///<summary>
        /// Cleans up the pair handler.
        ///</summary>
        public override void CleanUp()
        {

            base.CleanUp();

            sphereA = null;
            sphereB = null;




        }

        protected internal override void GetContactInformation(int index, out ContactInformation info)
        {
            info.Contact = ContactManifold.contacts.Elements[index];
            //Find the contact's force.
            info.FrictionImpulse = F64.C0;
            info.NormalImpulse = F64.C0;
            for (int i = 0; i < contactConstraint.frictionConstraints.Count; i++)
            {
                if (contactConstraint.frictionConstraints.Elements[i].PenetrationConstraint.contact == info.Contact)
                {
                    info.FrictionImpulse = contactConstraint.frictionConstraints.Elements[i].accumulatedImpulse;
                    info.NormalImpulse = contactConstraint.frictionConstraints.Elements[i].PenetrationConstraint.accumulatedImpulse;
                    break;
                }
            }
            //Compute relative velocity
            FPVector3 velocity;
            if (EntityA != null)
            {
                FPVector3.Subtract(ref info.Contact.Position, ref EntityA._position, out velocity);
                FPVector3.Cross(ref EntityA._angularVelocity, ref velocity, out velocity);
                FPVector3.Add(ref velocity, ref EntityA._linearVelocity, out info.RelativeVelocity);
            }
            else
                info.RelativeVelocity = new FPVector3();

            if (EntityB != null)
            {
                FPVector3.Subtract(ref info.Contact.Position, ref EntityB._position, out velocity);
                FPVector3.Cross(ref EntityB._angularVelocity, ref velocity, out velocity);
                FPVector3.Add(ref velocity, ref EntityB._linearVelocity, out velocity);
                FPVector3.Subtract(ref info.RelativeVelocity, ref velocity, out info.RelativeVelocity);
            }


            info.Pair = this;
        }

    }

}
