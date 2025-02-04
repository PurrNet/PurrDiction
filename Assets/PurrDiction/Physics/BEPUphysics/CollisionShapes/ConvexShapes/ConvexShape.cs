using System;
using BEPUphysics.CollisionTests.CollisionAlgorithms;
using BEPUutilities;
using BEPUphysics.Settings;
using FixMath.NET;

namespace BEPUphysics.CollisionShapes.ConvexShapes
{
    ///<summary>
    /// Superclass of convex collision shapes.
    ///</summary>
    public abstract class ConvexShape : EntityShape
    {

        protected void UpdateConvexShapeInfo(ConvexShapeDescription description)
        {
            UpdateEntityShapeVolume(description.EntityShapeVolume);
            MinimumRadius = description.MinimumRadius;
            MaximumRadius = description.MaximumRadius;
            collisionMargin = description.CollisionMargin;
        }



        protected internal FP collisionMargin = CollisionDetectionSettings.DefaultMargin;
        ///<summary>
        /// Collision margin of the convex shape.  The margin is a small spherical expansion around
        /// entities which allows specialized collision detection algorithms to be used.
        /// It's recommended that this be left unchanged.
        ///</summary>
        public FP CollisionMargin
        {
            get
            {
                return collisionMargin;
            }
            set
            {
                if (value < F64.C0)
                    throw new ArgumentException("Collision margin must be nonnegative..");
                collisionMargin = value;
                OnShapeChanged();
            }
        }

        /// <summary>
        /// Gets or sets the minimum radius of the collidable's shape.  This is initialized to a value that is
        /// guaranteed to be equal to or smaller than the actual minimum radius.  When setting this property,
        /// ensure that the inner sphere formed by the new minimum radius is fully contained within the shape.
        /// </summary>
        public FP MinimumRadius { get; internal set; }

        /// <summary>
        /// Gets the maximum radius of the collidable's shape.  This is initialized to a value that is
        /// guaranteed to be equal to or larger than the actual maximum radius.
        /// </summary>
        public FP MaximumRadius { get; internal set; }

        ///<summary>
        /// Gets the extreme point of the shape in local space in a given direction.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public abstract void GetLocalExtremePointWithoutMargin(ref FPVector3 direction, out FPVector3 extremePoint);

        ///<summary>
        /// Gets the extreme point of the shape in world space in a given direction.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        /// <param name="shapeTransform">Transform to use for the shape.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public void GetExtremePointWithoutMargin(FPVector3 direction, ref RigidTransform shapeTransform, out FPVector3 extremePoint)
        {
            FPQuaternion conjugate;
            FPQuaternion.Conjugate(ref shapeTransform.Orientation, out conjugate);
            FPQuaternion.Transform(ref direction, ref conjugate, out direction);
            GetLocalExtremePointWithoutMargin(ref direction, out extremePoint);

            FPQuaternion.Transform(ref extremePoint, ref shapeTransform.Orientation, out extremePoint);
            FPVector3.Add(ref extremePoint, ref shapeTransform.Position, out extremePoint);
        }

        ///<summary>
        /// Gets the extreme point of the shape in world space in a given direction with margin expansion.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        /// <param name="shapeTransform">Transform to use for the shape.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public void GetExtremePoint(FPVector3 direction, ref RigidTransform shapeTransform, out FPVector3 extremePoint)
        {
            GetExtremePointWithoutMargin(direction, ref shapeTransform, out extremePoint);

			FP directionLength = direction.LengthSquared();
            if (directionLength > Toolbox.Epsilon)
            {
                FPVector3.Multiply(ref direction, collisionMargin / FP.Sqrt(directionLength), out direction);
                FPVector3.Add(ref extremePoint, ref direction, out extremePoint);
            }

        }

        ///<summary>
        /// Gets the extreme point of the shape in local space in a given direction with margin expansion.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public void GetLocalExtremePoint(FPVector3 direction, out FPVector3 extremePoint)
        {
            GetLocalExtremePointWithoutMargin(ref direction, out extremePoint);

			FP directionLength = direction.LengthSquared();
            if (directionLength > Toolbox.Epsilon)
            {
                FPVector3.Multiply(ref direction, collisionMargin / FP.Sqrt(directionLength), out direction);
                FPVector3.Add(ref extremePoint, ref direction, out extremePoint);
            }
        }



        /// <summary>
        /// Gets the bounding box of the shape given a transform.
        /// </summary>
        /// <param name="shapeTransform">Transform to use.</param>
        /// <param name="boundingBox">Bounding box of the transformed shape.</param>
        public override void GetBoundingBox(ref RigidTransform shapeTransform, out FPBoundingBox boundingBox)
        {
#if !WINDOWS
            boundingBox = new FPBoundingBox();
#endif
            Matrix3x3 o;
            Matrix3x3.CreateFromQuaternion(ref shapeTransform.Orientation, out o);
            //Sample the local directions from the orientation matrix, implicitly transposed.

            FPVector3 right;
            var direction = new FPVector3(o.M11, o.M21, o.M31);
            GetLocalExtremePointWithoutMargin(ref direction, out right);

            FPVector3 left;
            direction = new FPVector3(-o.M11, -o.M21, -o.M31);
            GetLocalExtremePointWithoutMargin(ref direction, out left);

            FPVector3 up;
            direction = new FPVector3(o.M12, o.M22, o.M32);
            GetLocalExtremePointWithoutMargin(ref direction, out up);

            FPVector3 down;
            direction = new FPVector3(-o.M12, -o.M22, -o.M32);
            GetLocalExtremePointWithoutMargin(ref direction, out down);

            FPVector3 backward;
            direction = new FPVector3(o.M13, o.M23, o.M33);
            GetLocalExtremePointWithoutMargin(ref direction, out backward);

            FPVector3 forward;
            direction = new FPVector3(-o.M13, -o.M23, -o.M33);
            GetLocalExtremePointWithoutMargin(ref direction, out forward);


            //Rather than transforming each axis independently (and doing three times as many operations as required), just get the 6 required values directly.
            FPVector3 positive, negative;
            TransformLocalExtremePoints(ref right, ref up, ref backward, ref o, out positive);
            TransformLocalExtremePoints(ref left, ref down, ref forward, ref o, out negative);

            //The positive and negative vectors represent the X, Y and Z coordinates of the extreme points in world space along the world space axes.
            boundingBox.Max.x = shapeTransform.Position.x + positive.x + collisionMargin;
            boundingBox.Max.y = shapeTransform.Position.y + positive.y + collisionMargin;
            boundingBox.Max.z = shapeTransform.Position.z + positive.z + collisionMargin;

            boundingBox.Min.x = shapeTransform.Position.x + negative.x - collisionMargin;
            boundingBox.Min.y = shapeTransform.Position.y + negative.y - collisionMargin;
            boundingBox.Min.z = shapeTransform.Position.z + negative.z - collisionMargin;

        }

        /// <summary>
        /// Gets the intersection between the convex shape and the ray.
        /// </summary>
        /// <param name="ray">Ray to test.</param>
        /// <param name="transform">Transform of the convex shape.</param>
        /// <param name="maximumLength">Maximum distance to travel in units of the ray direction's length.</param>
        /// <param name="hit">Ray hit data, if any.</param>
        /// <returns>Whether or not the ray hit the target.</returns>
        public virtual bool RayTest(ref FPRay ray, ref RigidTransform transform, FP maximumLength, out FPRayHit hit)
        {
            return MPRToolbox.RayCast(ray, maximumLength, this, ref transform, out hit);
        }

        /// <summary>
        /// Computes a bounding box for the shape and expands it.
        /// </summary>
        /// <param name="transform">Transform to use to position the shape.</param>
        /// <param name="sweep">Extra to add to the bounding box.</param>
        /// <param name="boundingBox">Expanded bounding box.</param>
        public void GetSweptBoundingBox(ref RigidTransform transform, ref FPVector3 sweep, out FPBoundingBox boundingBox)
        {
            GetBoundingBox(ref transform, out boundingBox);
            Toolbox.ExpandBoundingBox(ref boundingBox, ref sweep);

        }

        /// <summary>
        /// Gets the bounding box of the convex shape transformed first into world space, and then into the local space of another affine transform.
        /// </summary>
        /// <param name="shapeTransform">Transform to use to put the shape into world space.</param>
        /// <param name="spaceTransform">Used as the frame of reference to compute the bounding box.
        /// In effect, the shape is transformed by the inverse of the space transform to compute its bounding box in local space.</param>
        /// <param name="sweep">Vector to expand the bounding box with in local space.</param>
        /// <param name="boundingBox">Bounding box in the local space.</param>
        public void GetSweptLocalBoundingBox(ref RigidTransform shapeTransform, ref AffineTransform spaceTransform, ref FPVector3 sweep, out FPBoundingBox boundingBox)
        {
            GetLocalBoundingBox(ref shapeTransform, ref spaceTransform, out boundingBox);
            FPVector3 expansion;
            Matrix3x3.TransformTranspose(ref sweep, ref spaceTransform.LinearTransform, out expansion);
            Toolbox.ExpandBoundingBox(ref boundingBox, ref expansion);
        }

        //Transform the convex into the space of something else.
        /// <summary>
        /// Gets the bounding box of the convex shape transformed first into world space, and then into the local space of another affine transform.
        /// </summary>
        /// <param name="shapeTransform">Transform to use to put the shape into world space.</param>
        /// <param name="spaceTransform">Used as the frame of reference to compute the bounding box.
        /// In effect, the shape is transformed by the inverse of the space transform to compute its bounding box in local space.</param>
        /// <param name="boundingBox">Bounding box in the local space.</param>
        public void GetLocalBoundingBox(ref RigidTransform shapeTransform, ref AffineTransform spaceTransform, out FPBoundingBox boundingBox)
        {
#if !WINDOWS
            boundingBox = new FPBoundingBox();
#endif
            //TODO: This method peforms quite a few sqrts because the collision margin can get scaled, and so cannot be applied as a final step.
            //There should be a better way to do this. At the very least, it should be possible to avoid the 6 square roots involved currently.
            //If this shows a a bottleneck, it might be best to virtualize this function and implement a per-shape variant.
            //Also... It might be better just to have the internal function be a GetBoundingBox that takes an AffineTransform, and an outer function
            //does the local space fiddling.

            //Move forward into convex's space, backwards into the new space's local space.
            AffineTransform transform;
            AffineTransform.Invert(ref spaceTransform, out transform);
            AffineTransform.Multiply(ref shapeTransform, ref transform, out transform);

            //Sample the local directions from the orientation matrix, implicitly transposed.

            FPVector3 right;
            var direction = new FPVector3(transform.LinearTransform.M11, transform.LinearTransform.M21, transform.LinearTransform.M31);
            GetLocalExtremePoint(direction, out right);

            FPVector3 left;
            direction = new FPVector3(-transform.LinearTransform.M11, -transform.LinearTransform.M21, -transform.LinearTransform.M31);
            GetLocalExtremePoint(direction, out left);

            FPVector3 up;
            direction = new FPVector3(transform.LinearTransform.M12, transform.LinearTransform.M22, transform.LinearTransform.M32);
            GetLocalExtremePoint(direction, out up);

            FPVector3 down;
            direction = new FPVector3(-transform.LinearTransform.M12, -transform.LinearTransform.M22, -transform.LinearTransform.M32);
            GetLocalExtremePoint(direction, out down);

            FPVector3 backward;
            direction = new FPVector3(transform.LinearTransform.M13, transform.LinearTransform.M23, transform.LinearTransform.M33);
            GetLocalExtremePoint(direction, out backward);

            FPVector3 forward;
            direction = new FPVector3(-transform.LinearTransform.M13, -transform.LinearTransform.M23, -transform.LinearTransform.M33);
            GetLocalExtremePoint(direction, out forward);

            //Rather than transforming each axis independently (and doing three times as many operations as required), just get the 6 required values directly.
            FPVector3 positive, negative;
            TransformLocalExtremePoints(ref right, ref up, ref backward, ref transform.LinearTransform, out positive);
            TransformLocalExtremePoints(ref left, ref down, ref forward, ref transform.LinearTransform, out negative);

            //The positive and negative vectors represent the X, Y and Z coordinates of the extreme points in world space along the world space axes.
            boundingBox.Max.x = transform.Translation.x + positive.x;
            boundingBox.Max.y = transform.Translation.y + positive.y;
            boundingBox.Max.z = transform.Translation.z + positive.z;

            boundingBox.Min.x = transform.Translation.x + negative.x;
            boundingBox.Min.y = transform.Translation.y + negative.y;
            boundingBox.Min.z = transform.Translation.z + negative.z;
        }


    }
}
