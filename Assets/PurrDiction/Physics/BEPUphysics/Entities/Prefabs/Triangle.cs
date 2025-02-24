using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.EntityStateManagement;
 
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Entities.Prefabs
{
    /// <summary>
    /// Triangle-shaped object that can collide and move.  After making an entity, add it to a Space so that the engine can manage it.
    /// </summary>
    public class Triangle : Entity<ConvexCollidable<TriangleShape>>
    {

        ///<summary>
        /// Gets or sets the first vertex of the triangle in local space.
        ///</summary>
        public FPVector3 LocalVertexA
        {
            get
            {
                return CollisionInformation.Shape.VertexA;
            }
            set
            {
                CollisionInformation.Shape.VertexA = value;
            }
        }
        ///<summary>
        /// Gets or sets the second vertex of the triangle in local space.
        ///</summary>
        public FPVector3 LocalVertexB
        {
            get
            {
                return CollisionInformation.Shape.VertexB;
            }
            set
            {
                CollisionInformation.Shape.VertexB = value;
            }
        }
        ///<summary>
        /// Gets or sets the third vertex of the triangle in local space.
        ///</summary>
        public FPVector3 LocalVertexC
        {
            get
            {
                return CollisionInformation.Shape.VertexC;
            }
            set
            {
                CollisionInformation.Shape.VertexC = value;
            }
        }


        ///<summary>
        /// Gets or sets the first vertex of the triangle in world space.
        ///</summary>
        public FPVector3 VertexA
        {
            get
            {
                return Matrix3x3.Transform(CollisionInformation.Shape.VertexA, _orientationMatrix) + _position;
            }
            set
            {
                CollisionInformation.Shape.VertexA = Matrix3x3.TransformTranspose(value - _position, _orientationMatrix);
            }
        }
        ///<summary>
        /// Gets or sets the second vertex of the triangle in world space.
        ///</summary>
        public FPVector3 VertexB
        {
            get
            {
                return Matrix3x3.Transform(CollisionInformation.Shape.VertexB, _orientationMatrix) + _position;
            }
            set
            {
                CollisionInformation.Shape.VertexB = Matrix3x3.TransformTranspose(value - _position, _orientationMatrix);
            }
        }
        ///<summary>
        /// Gets or sets the third vertex of the triangle in world space.
        ///</summary>
        public FPVector3 VertexC
        {
            get
            {
                return Matrix3x3.Transform(CollisionInformation.Shape.VertexC, _orientationMatrix) + _position;
            }
            set
            {
                CollisionInformation.Shape.VertexC = Matrix3x3.TransformTranspose(value - _position, _orientationMatrix);
            }
        }

        ///<summary>
        /// Gets or sets the sidedness of the triangle.
        ///</summary>
        public TriangleSidedness Sidedness
        {
            get { return CollisionInformation.Shape.Sidedness; }
            set
            {
                CollisionInformation.Shape.Sidedness = value;
            }
        }



        /// <summary>
        /// Constructs a dynamic triangle.
        /// </summary>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        /// <param name="mass">Mass of the object.</param>
        public Triangle(FPVector3 v1, FPVector3 v2, FPVector3 v3, FP mass)
        {
            FPVector3 center;
            var shape = new TriangleShape(v1, v2, v3, out center);
            Initialize(new ConvexCollidable<TriangleShape>(shape), mass);
            position = center;
        }

        /// <summary>
        /// Constructs a nondynamic triangle.
        /// </summary>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        public Triangle(FPVector3 v1, FPVector3 v2, FPVector3 v3)
        {
            FPVector3 center;
            var shape = new TriangleShape(v1, v2, v3, out center);
            Initialize(new ConvexCollidable<TriangleShape>(shape));
            position = center;
        }

        /// <summary>
        /// Constructs a dynamic triangle.
        /// </summary>
        /// <param name="pos">Position where the triangle is initialy centered.</param>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        /// <param name="mass">Mass of the object.</param>
        public Triangle(FPVector3 pos, FPVector3 v1, FPVector3 v2, FPVector3 v3, FP mass)
            : this(v1, v2, v3, mass)
        {
            position = pos;
        }

        /// <summary>
        /// Constructs a nondynamic triangle.
        /// </summary>
        /// <param name="pos">Position where the triangle is initially centered.</param>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        public Triangle(FPVector3 pos, FPVector3 v1, FPVector3 v2, FPVector3 v3)
            : this(v1, v2, v3)
        {
            position = pos;
        }

        /// <summary>
        /// Constructs a dynamic triangle.
        /// </summary>
        /// <param name="motionState">Motion state specifying the entity's initial state.</param>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        /// <param name="mass">Mass of the object.</param>
        public Triangle(MotionState motionState, FPVector3 v1, FPVector3 v2, FPVector3 v3, FP mass)
            : this(v1, v2, v3, mass)
        {
            MotionState = motionState;
        }

        /// <summary>
        /// Constructs a nondynamic triangle.
        /// </summary>
        /// <param name="motionState">Motion state specifying the entity's initial state.</param>
        /// <param name="v1">Position of the first vertex.</param>
        /// <param name="v2">Position of the second vertex.</param>
        /// <param name="v3">Position of the third vertex.</param>
        public Triangle(MotionState motionState, FPVector3 v1, FPVector3 v2, FPVector3 v3)
            : this(v1, v2, v3)
        {
            MotionState = motionState;
        }




    }
}