using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.EntityStateManagement;
 
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Entities.Prefabs
{
    /// <summary>
    /// Box-shaped object that can collide and move.  After making an entity, add it to a Space so that the engine can manage it.
    /// </summary>
    public class Box : Entity<ConvexCollidable<BoxShape>>
    {

        private Box(FP width, FP height, FP length)
            :base(new ConvexCollidable<BoxShape>(new BoxShape(width, height, length)))
        {
        }

        private Box(FP width, FP height, FP length, FP mass)
            :base(new ConvexCollidable<BoxShape>(new BoxShape(width, height, length)), mass)
        {
        }

        /// <summary>
        /// Constructs a physically simulated box.
        /// </summary>
        /// <param name="pos">Position of the box.</param>
        /// <param name="width">Width of the box.</param>
        /// <param name="length">Length of the box.</param>
        /// <param name="height">Height of the box.</param>
        /// <param name="mass">Mass of the object.</param>
        public Box(FPVector3 pos, FP width, FP height, FP length, FP mass)
            : this(width, height, length, mass)
        {
            Position = pos;
        }

        /// <summary>
        /// Constructs a nondynamic box.
        /// </summary>
        /// <param name="pos">Position of the box.</param>
        /// <param name="width">Width of the box.</param>
        /// <param name="length">Length of the box.</param>
        /// <param name="height">Height of the box.</param>
        public Box(FPVector3 pos, FP width, FP height, FP length)
            : this(width, height, length)
        {
            Position = pos;
        }

        /// <summary>
        /// Constructs a physically simulated box.
        /// </summary>
        /// <param name="motionState">Motion state specifying the entity's initial state.</param>
        /// <param name="width">Width of the box.</param>
        /// <param name="length">Length of the box.</param>
        /// <param name="height">Height of the box.</param>
        /// <param name="mass">Mass of the object.</param>
        public Box(MotionState motionState, FP width, FP height, FP length, FP mass)
            : this(width, height, length, mass)
        {
            MotionState = motionState;
        }



        /// <summary>
        /// Constructs a nondynamic box.
        /// </summary>
        /// <param name="motionState">Motion state specifying the entity's initial state.</param>
        /// <param name="width">Width of the box.</param>
        /// <param name="length">Length of the box.</param>
        /// <param name="height">Height of the box.</param>
        public Box(MotionState motionState, FP width, FP height, FP length)
            : this(width, height, length)
        {
            MotionState = motionState;
        }

        /// <summary>
        /// Width of the box divided by two.
        /// </summary>
        public FP HalfWidth
        {
            get { return CollisionInformation.Shape.HalfWidth; }
            set { CollisionInformation.Shape.HalfWidth = value; }
        }


        /// <summary>
        /// Height of the box divided by two.
        /// </summary>
        public FP HalfHeight
        {
            get { return CollisionInformation.Shape.HalfHeight; }
            set { CollisionInformation.Shape.HalfHeight = value; }
        }

        /// <summary>
        /// Length of the box divided by two.
        /// </summary>
        public FP HalfLength
        {
            get { return CollisionInformation.Shape.HalfLength; }
            set { CollisionInformation.Shape.HalfLength = value; }
        }



        /// <summary>
        /// Width of the box.
        /// </summary>
        public FP Width
        {
            get { return CollisionInformation.Shape.Width; }
            set { CollisionInformation.Shape.Width = value; }
        }

        /// <summary>
        /// Height of the box.
        /// </summary>
        public FP Height
        {
            get { return CollisionInformation.Shape.Height; }
            set { CollisionInformation.Shape.Height = value; }
        }

        /// <summary>
        /// Length of the box.
        /// </summary>
        public FP Length
        {
            get { return CollisionInformation.Shape.Length; }
            set { CollisionInformation.Shape.Length = value; }
        }



    }
}