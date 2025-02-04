using FixMath.NET;
using System;

namespace BEPUutilities
{
    /// <summary>
    /// Provides XNA-like ray functionality.
    /// </summary>
    public struct FPRay
    {
        /// <summary>
        /// Starting position of the ray.
        /// </summary>
        public FPVector3 Position;
        /// <summary>
        /// Direction in which the ray points.
        /// </summary>
        public FPVector3 Direction;


        /// <summary>
        /// Constructs a new ray.
        /// </summary>
        /// <param name="position">Starting position of the ray.</param>
        /// <param name="direction">Direction in which the ray points.</param>
        public FPRay(FPVector3 position, FPVector3 direction)
        {
            this.Position = position;
            this.Direction = direction;
        }



        /// <summary>
        /// Determines if and when the ray intersects the bounding box.
        /// </summary>
        /// <param name="boundingBox">Bounding box to test against.</param>
        /// <param name="t">The length along the ray to the impact, if any impact occurs.</param>
        /// <returns>True if the ray intersects the target, false otherwise.</returns>
        public bool Intersects(ref FPBoundingBox boundingBox, out FP t)
        {
			FP tmin = F64.C0, tmax = FP.MaxValue;
            if (FP.Abs(Direction.X) < Toolbox.Epsilon)
            {
                if (Position.X < boundingBox.Min.X || Position.X > boundingBox.Max.X)
                {
                    //If the ray isn't pointing along the axis at all, and is outside of the box's interval, then it
                    //can't be intersecting.
                    t = F64.C0;
                    return false;
                }
            }
            else
            {
                var inverseDirection = F64.C1 / Direction.X;
                var t1 = (boundingBox.Min.X - Position.X) * inverseDirection;
                var t2 = (boundingBox.Max.X - Position.X) * inverseDirection;
                if (t1 > t2)
                {
					FP temp = t1;
                    t1 = t2;
                    t2 = temp;
                }
                tmin = MathHelper.Max(tmin, t1);
                tmax = MathHelper.Min(tmax, t2);
                if (tmin > tmax)
                {
                    t = F64.C0;
                    return false;
                }
            }
            if (FP.Abs(Direction.Y) < Toolbox.Epsilon)
            {
                if (Position.Y < boundingBox.Min.Y || Position.Y > boundingBox.Max.Y)
                {
                    //If the ray isn't pointing along the axis at all, and is outside of the box's interval, then it
                    //can't be intersecting.
                    t = F64.C0;
                    return false;
                }
            }
            else
            {
                var inverseDirection = F64.C1 / Direction.Y;
                var t1 = (boundingBox.Min.Y - Position.Y) * inverseDirection;
                var t2 = (boundingBox.Max.Y - Position.Y) * inverseDirection;
                if (t1 > t2)
                {
					FP temp = t1;
                    t1 = t2;
                    t2 = temp;
                }
                tmin = MathHelper.Max(tmin, t1);
                tmax = MathHelper.Min(tmax, t2);
                if (tmin > tmax)
                {
                    t = F64.C0;
                    return false;
                }
            }
            if (FP.Abs(Direction.Z) < Toolbox.Epsilon)
            {
                if (Position.Z < boundingBox.Min.Z || Position.Z > boundingBox.Max.Z)
                {
                    //If the ray isn't pointing along the axis at all, and is outside of the box's interval, then it
                    //can't be intersecting.
                    t = F64.C0;
                    return false;
                }
            }
            else
            {
                var inverseDirection = F64.C1 / Direction.Z;
                var t1 = (boundingBox.Min.Z - Position.Z) * inverseDirection;
                var t2 = (boundingBox.Max.Z - Position.Z) * inverseDirection;
                if (t1 > t2)
                {
					FP temp = t1;
                    t1 = t2;
                    t2 = temp;
                }
                tmin = MathHelper.Max(tmin, t1);
                tmax = MathHelper.Min(tmax, t2);
                if (tmin > tmax)
                {
                    t = F64.C0;
                    return false;
                }
            }
            t = tmin;
            return true;
        }

        /// <summary>
        /// Determines if and when the ray intersects the bounding box.
        /// </summary>
        /// <param name="boundingBox">Bounding box to test against.</param>
        /// <param name="t">The length along the ray to the impact, if any impact occurs.</param>
        /// <returns>True if the ray intersects the target, false otherwise.</returns>
        public bool Intersects(FPBoundingBox boundingBox, out FP t)
        {
            return Intersects(ref boundingBox, out t);
        }

        /// <summary>
        /// Determines if and when the ray intersects the plane.
        /// </summary>
        /// <param name="plane">Plane to test against.</param>
        /// <param name="t">The length along the ray to the impact, if any impact occurs.</param>
        /// <returns>True if the ray intersects the target, false otherwise.</returns>
        public bool Intersects(ref FPPlane plane, out FP t)
        {
			FP velocity;
            FPVector3.Dot(ref Direction, ref plane.Normal, out velocity);
            if (FP.Abs(velocity) < Toolbox.Epsilon)
            {
                t = F64.C0;
                return false;
            }
			FP distanceAlongNormal;
            FPVector3.Dot(ref Position, ref plane.Normal, out distanceAlongNormal);
            distanceAlongNormal += plane.D;
            t = -distanceAlongNormal / velocity;
            return t >= -Toolbox.Epsilon;
        }

        /// <summary>
        /// Determines if and when the ray intersects the plane.
        /// </summary>
        /// <param name="plane">Plane to test against.</param>
        /// <param name="t">The length along the ray to the impact, if any impact occurs.</param>
        /// <returns>True if the ray intersects the target, false otherwise.</returns>
        public bool Intersects(FPPlane plane, out FP t)
        {
            return Intersects(ref plane, out t);
        }

        /// <summary>
        /// Computes a point along a ray given the length along the ray from the ray position.
        /// </summary>
        /// <param name="t">Length along the ray from the ray position in terms of the ray's direction.</param>
        /// <param name="v">Point along the ray at the given location.</param>
        public void GetPointOnRay(FP t, out FPVector3 v)
        {
            FPVector3.Multiply(ref Direction, t, out v);
            FPVector3.Add(ref v, ref Position, out v);
        }
    }
}
