using System;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.CollisionShapes.ConvexShapes
{
    ///<summary>
    /// Convex shape with width, length, and height.
    ///</summary>
    public class BoxShape : ConvexShape
    {
        internal FP halfWidth;
        internal FP halfHeight;
        internal FP halfLength;


        /// <summary>
        /// Width of the box divided by two.
        /// </summary>
        public FP HalfWidth
        {
            get { return halfWidth; }
            set { halfWidth = value; OnShapeChanged(); }
        }

        /// <summary>
        /// Height of the box divided by two.
        /// </summary>
        public FP HalfHeight
        {
            get { return halfHeight; }
            set { halfHeight = value; OnShapeChanged(); }
        }

        /// <summary>
        /// Length of the box divided by two.
        /// </summary>
        public FP HalfLength
        {
            get { return halfLength; }
            set { halfLength = value; OnShapeChanged(); }
        }

        /// <summary>
        /// Width of the box.
        /// </summary>
        public FP Width
        {
            get { return halfWidth * F64.C2; }
            set { halfWidth = value * F64.C0p5; OnShapeChanged(); }
        }

        /// <summary>
        /// Height of the box.
        /// </summary>
        public FP Height
        {
            get { return halfHeight * F64.C2; }
            set { halfHeight = value * F64.C0p5; OnShapeChanged(); }
        }

        /// <summary>
        /// Length of the box.
        /// </summary>
        public FP Length
        {
            get { return halfLength * F64.C2; }
            set { halfLength = value * F64.C0p5; OnShapeChanged(); }
        }


        ///<summary>
        /// Constructs a new box shape.
        ///</summary>
        ///<param name="width">Width of the box.</param>
        ///<param name="height">Height of the box.</param>
        ///<param name="length">Length of the box.</param>
        public BoxShape(FP width, FP height, FP length)
        {
            halfWidth = width * F64.C0p5;
            halfHeight = height * F64.C0p5;
            halfLength = length * F64.C0p5;

            UpdateConvexShapeInfo(ComputeDescription(width, height, length, collisionMargin));
        }

        ///<summary>
        /// Constructs a new box shape from cached information.
        ///</summary>
        ///<param name="width">Width of the box.</param>
        ///<param name="height">Height of the box.</param>
        ///<param name="length">Length of the box.</param>
        /// <param name="description">Cached information about the shape. Assumed to be correct; no extra processing or validation is performed.</param>
        public BoxShape(FP width, FP height, FP length, ConvexShapeDescription description)
        {
            halfWidth = width * F64.C0p5;
            halfHeight = height * F64.C0p5;
            halfLength = length * F64.C0p5;

            UpdateConvexShapeInfo(description);
        }

        protected override void OnShapeChanged()
        {
            UpdateConvexShapeInfo(ComputeDescription(halfWidth, halfHeight, halfLength, collisionMargin));
            base.OnShapeChanged();
        }

        /// <summary>
        /// Computes a convex shape description for a BoxShape.
        /// </summary>
        ///<param name="width">Width of the box.</param>
        ///<param name="height">Height of the box.</param>
        ///<param name="length">Length of the box.</param>
        /// <param name="collisionMargin">Collision margin of the shape.</param>
        /// <returns>Description required to define a convex shape.</returns>
        public static ConvexShapeDescription ComputeDescription(FP width, FP height, FP length, FP collisionMargin)
        {
            ConvexShapeDescription description;
            description.EntityShapeVolume.Volume = width * height * length;

            FP widthSquared = width * width;
            FP heightSquared = height * height;
            FP lengthSquared = length * length;
			FP inv12 = F64.OneTwelfth;

            description.EntityShapeVolume.VolumeDistribution = new Matrix3x3();
            description.EntityShapeVolume.VolumeDistribution.M11 = (heightSquared + lengthSquared) * inv12;
            description.EntityShapeVolume.VolumeDistribution.M22 = (widthSquared + lengthSquared) * inv12;
            description.EntityShapeVolume.VolumeDistribution.M33 = (widthSquared + heightSquared) * inv12;

            description.MaximumRadius = F64.C0p5 * FP.Sqrt(width * width + height * height + length * length);
            description.MinimumRadius = F64.C0p5 * MathHelper.Min(width, MathHelper.Min(height, length));

            description.CollisionMargin = collisionMargin;
            return description;
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
            //Notice only three directions are used.  Due to box symmetry, 'left' is just -right.
            var right = new FPVector3(FP.Sign(o.M11) * halfWidth, FP.Sign(o.M21) * halfHeight, FP.Sign(o.M31) * halfLength);

            var up = new FPVector3(FP.Sign(o.M12) * halfWidth, FP.Sign(o.M22) * halfHeight, FP.Sign(o.M32) * halfLength);

            var backward = new FPVector3(FP.Sign(o.M13) * halfWidth, FP.Sign(o.M23) * halfHeight, FP.Sign(o.M33) * halfLength);


            //Rather than transforming each axis independently (and doing three times as many operations as required), just get the 3 required values directly.
            FPVector3 offset;
            TransformLocalExtremePoints(ref right, ref up, ref backward, ref o, out offset);

            //The positive and negative vectors represent the X, Y and Z coordinates of the extreme points in world space along the world space axes.
            FPVector3.Add(ref shapeTransform.Position, ref offset, out boundingBox.Max);
            FPVector3.Subtract(ref shapeTransform.Position, ref offset, out boundingBox.Min);

        }


        ///<summary>
        /// Gets the extreme point of the shape in local space in a given direction.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public override void GetLocalExtremePointWithoutMargin(ref FPVector3 direction, out FPVector3 extremePoint)
        {
            extremePoint = new FPVector3(FP.Sign(direction.x) * (halfWidth - collisionMargin), FP.Sign(direction.y) * (halfHeight - collisionMargin), FP.Sign(direction.z) * (halfLength - collisionMargin));
        }




        /// <summary>
        /// Gets the intersection between the box and the ray.
        /// </summary>
        /// <param name="ray">Ray to test against the box.</param>
        /// <param name="transform">Transform of the shape.</param>
        /// <param name="maximumLength">Maximum distance to travel in units of the direction vector's length.</param>
        /// <param name="hit">Hit data for the raycast, if any.</param>
        /// <returns>Whether or not the ray hit the target.</returns>
        public override bool RayTest(ref FPRay ray, ref RigidTransform transform, FP maximumLength, out FPRayHit hit)
        {
            hit = new FPRayHit();

            FPQuaternion conjugate;
            FPQuaternion.Conjugate(ref transform.Orientation, out conjugate);
            FPVector3 localOrigin;
            FPVector3.Subtract(ref ray.Position, ref transform.Position, out localOrigin);
            FPQuaternion.Transform(ref localOrigin, ref conjugate, out localOrigin);
            FPVector3 localDirection;
            FPQuaternion.Transform(ref ray.Direction, ref conjugate, out localDirection);
            FPVector3 normal = Toolbox.ZeroVector;
            FP temp, tmin = F64.C0, tmax = maximumLength;

            if (FP.Abs(localDirection.x) < Toolbox.Epsilon && (localOrigin.x < -halfWidth || localOrigin.x > halfWidth))
                return false;
            FP inverseDirection = F64.C1 / localDirection.x;
			// inverseDirection might be Infinity (Fix64.MaxValue), so use SafeMul here to handle overflow
            FP t1 = FP.SafeMul((-halfWidth - localOrigin.x), inverseDirection);
            FP t2 = FP.SafeMul((halfWidth - localOrigin.x), inverseDirection);
            var tempNormal = new FPVector3(-1, F64.C0, F64.C0);
            if (t1 > t2)
            {
                temp = t1;
                t1 = t2;
                t2 = temp;
                tempNormal *= -1;
            }
            temp = tmin;
            tmin = MathHelper.Max(tmin, t1);
            if (temp != tmin)
                normal = tempNormal;
            tmax = MathHelper.Min(tmax, t2);
            if (tmin > tmax)
                return false;
            if (FP.Abs(localDirection.y) < Toolbox.Epsilon && (localOrigin.y < -halfHeight || localOrigin.y > halfHeight))
                return false;
            inverseDirection = F64.C1 / localDirection.y;
            t1 = FP.SafeMul((-halfHeight - localOrigin.y), inverseDirection);
            t2 = FP.SafeMul((halfHeight - localOrigin.y), inverseDirection);
            tempNormal = new FPVector3(F64.C0, -1, F64.C0);
            if (t1 > t2)
            {
                temp = t1;
                t1 = t2;
                t2 = temp;
                tempNormal *= -1;
            }
            temp = tmin;
            tmin = MathHelper.Max(tmin, t1);
            if (temp != tmin)
                normal = tempNormal;
            tmax = MathHelper.Min(tmax, t2);
            if (tmin > tmax)
                return false;
            if (FP.Abs(localDirection.z) < Toolbox.Epsilon && (localOrigin.z < -halfLength || localOrigin.z > halfLength))
                return false;
            inverseDirection = F64.C1 / localDirection.z;
            t1 = FP.SafeMul((-halfLength - localOrigin.z), inverseDirection);
            t2 = FP.SafeMul((halfLength - localOrigin.z), inverseDirection);
            tempNormal = new FPVector3(F64.C0, F64.C0, -1);
            if (t1 > t2)
            {
                temp = t1;
                t1 = t2;
                t2 = temp;
                tempNormal *= -1;
            }
            temp = tmin;
            tmin = MathHelper.Max(tmin, t1);
            if (temp != tmin)
                normal = tempNormal;
            tmax = MathHelper.Min(tmax, t2);
            if (tmin > tmax)
                return false;
            hit.T = tmin;
            FPVector3.Multiply(ref ray.Direction, tmin, out hit.Location);
            FPVector3.Add(ref hit.Location, ref ray.Position, out hit.Location);
            FPQuaternion.Transform(ref normal, ref transform.Orientation, out normal);
            hit.Normal = normal;
            return true;
        }

        /// <summary>
        /// Retrieves an instance of an EntityCollidable that uses this EntityShape.  Mainly used by compound bodies.
        /// </summary>
        /// <returns>EntityCollidable that uses this shape.</returns>
        public override EntityCollidable GetCollidableInstance()
        {
            return new ConvexCollidable<BoxShape>(this);
        }

    }
}
