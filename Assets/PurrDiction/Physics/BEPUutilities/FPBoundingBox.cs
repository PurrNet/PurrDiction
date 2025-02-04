using FixMath.NET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BEPUutilities
{
    /// <summary>
    /// Provides XNA-like axis-aligned bounding box functionality.
    /// </summary>
    public struct FPBoundingBox
    {
        /// <summary>
        /// Location with the lowest X, Y, and Z coordinates in the axis-aligned bounding box.
        /// </summary>
        public FPVector3 Min;

        /// <summary>
        /// Location with the highest X, Y, and Z coordinates in the axis-aligned bounding box.
        /// </summary>
        public FPVector3 Max;

        /// <summary>
        /// Constructs a bounding box from the specified minimum and maximum.
        /// </summary>
        /// <param name="min">Location with the lowest X, Y, and Z coordinates contained by the axis-aligned bounding box.</param>
        /// <param name="max">Location with the highest X, Y, and Z coordinates contained by the axis-aligned bounding box.</param>
        public FPBoundingBox(FPVector3 min, FPVector3 max)
        {
            this.Min = min;
            this.Max = max;
        }

        /// <summary>
        /// Gets an array of locations corresponding to the 8 corners of the bounding box.
        /// </summary>
        /// <returns>Corners of the bounding box.</returns>
        public FPVector3[] GetCorners()
        {
            var toReturn = new FPVector3[8];
            toReturn[0] = new FPVector3(Min.x, Max.y, Max.z);
            toReturn[1] = Max;
            toReturn[2] = new FPVector3(Max.x, Min.y, Max.z);
            toReturn[3] = new FPVector3(Min.x, Min.y, Max.z);
            toReturn[4] = new FPVector3(Min.x, Max.y, Min.z);
            toReturn[5] = new FPVector3(Max.x, Max.y, Min.z);
            toReturn[6] = new FPVector3(Max.x, Min.y, Min.z);
            toReturn[7] = Min;
            return toReturn;
        }


        /// <summary>
        /// Determines if a bounding box intersects another bounding box.
        /// </summary>
        /// <param name="boundingBox">Bounding box to test against.</param>
        /// <returns>Whether the bounding boxes intersected.</returns>
        public bool Intersects(FPBoundingBox boundingBox)
        {
            if (boundingBox.Min.x > Max.x || boundingBox.Min.y > Max.y || boundingBox.Min.z > Max.z)
                return false;
            if (Min.x > boundingBox.Max.x || Min.y > boundingBox.Max.y || Min.z > boundingBox.Max.z)
                return false;
            return true;

        }

        /// <summary>
        /// Determines if a bounding box intersects another bounding box.
        /// </summary>
        /// <param name="boundingBox">Bounding box to test against.</param>
        /// <param name="intersects">Whether the bounding boxes intersect.</param>
        public void Intersects(ref FPBoundingBox boundingBox, out bool intersects)
        {
            if (boundingBox.Min.x > Max.x || boundingBox.Min.y > Max.y || boundingBox.Min.z > Max.z)
            {
                intersects = false;
                return;
            }
            if (Min.x > boundingBox.Max.x || Min.y > boundingBox.Max.y || Min.z > boundingBox.Max.z)
            {
                intersects = false;
                return;
            }
            intersects = true;
        }

        /// <summary>
        /// Determines if a bounding box intersects a bounding sphere.
        /// </summary>
        /// <param name="boundingSphere">Sphere to test for intersection.</param>
        /// <param name="intersects">Whether the bounding shapes intersect.</param>
        public void Intersects(ref FPBoundingSphere boundingSphere, out bool intersects)
        {
            FPVector3 clampedLocation;
            if (boundingSphere.Center.x > Max.x)
                clampedLocation.x = Max.x;
            else if (boundingSphere.Center.x < Min.x)
                clampedLocation.x = Min.x;
            else
                clampedLocation.x = boundingSphere.Center.x;

            if (boundingSphere.Center.y > Max.y)
                clampedLocation.y = Max.y;
            else if (boundingSphere.Center.y < Min.y)
                clampedLocation.y = Min.y;
            else
                clampedLocation.y = boundingSphere.Center.y;

            if (boundingSphere.Center.z > Max.z)
                clampedLocation.z = Max.z;
            else if (boundingSphere.Center.z < Min.z)
                clampedLocation.z = Min.z;
            else
                clampedLocation.z = boundingSphere.Center.z;

			FP distanceSquared;
            FPVector3.DistanceSquared(ref clampedLocation, ref boundingSphere.Center, out distanceSquared);
            intersects = distanceSquared <= boundingSphere.Radius * boundingSphere.Radius;

        }

        //public bool Intersects(BoundingFrustum frustum)
        //{
        //    bool intersects;
        //    frustum.Intersects(ref this, out intersects);
        //    return intersects;
        //}

        public ContainmentType Contains(ref FPBoundingBox boundingBox)
        {
            if (Max.x < boundingBox.Min.x || Min.x > boundingBox.Max.x ||
                Max.y < boundingBox.Min.y || Min.y > boundingBox.Max.y ||
                Max.z < boundingBox.Min.z || Min.z > boundingBox.Max.z)
                return ContainmentType.Disjoint;
            //It is known to be at least intersecting. Is it contained?
            if (Min.x <= boundingBox.Min.x && Max.x >= boundingBox.Max.x &&
                Min.y <= boundingBox.Min.y && Max.y >= boundingBox.Max.y &&
                Min.z <= boundingBox.Min.z && Max.z >= boundingBox.Max.z)
                return ContainmentType.Contains;
            return ContainmentType.Intersects;
        }



        /// <summary>
        /// Creates the smallest possible bounding box that contains a list of points.
        /// </summary>
        /// <param name="points">Points to enclose with a bounding box.</param>
        /// <returns>Bounding box which contains the list of points.</returns>
        public static FPBoundingBox CreateFromPoints(IList<FPVector3> points)
        {
            FPBoundingBox aabb;
            if (points.Count == 0)
                throw new Exception("Cannot construct a bounding box from an empty list.");
            aabb.Min = points[0];
            aabb.Max = aabb.Min;
            for (int i = points.Count - 1; i >= 1; i--)
            {
                FPVector3 v = points[i];
                if (v.x < aabb.Min.x)
                    aabb.Min.x = v.x;
                else if (v.x > aabb.Max.x)
                    aabb.Max.x = v.x;

                if (v.y < aabb.Min.y)
                    aabb.Min.y = v.y;
                else if (v.y > aabb.Max.y)
                    aabb.Max.y = v.y;

                if (v.z < aabb.Min.z)
                    aabb.Min.z = v.z;
                else if (v.z > aabb.Max.z)
                    aabb.Max.z = v.z;
            }
            return aabb;
        }



        /// <summary>
        /// Creates the smallest bounding box which contains two other bounding boxes.
        /// </summary>
        /// <param name="a">First bounding box to be contained.</param>
        /// <param name="b">Second bounding box to be contained.</param>
        /// <param name="merged">Smallest bounding box which contains the two input bounding boxes.</param>
        public static void CreateMerged(ref FPBoundingBox a, ref FPBoundingBox b, out FPBoundingBox merged)
        {
            if (a.Min.x < b.Min.x)
                merged.Min.x = a.Min.x;
            else
                merged.Min.x = b.Min.x;
            if (a.Min.y < b.Min.y)
                merged.Min.y = a.Min.y;
            else
                merged.Min.y = b.Min.y;
            if (a.Min.z < b.Min.z)
                merged.Min.z = a.Min.z;
            else
                merged.Min.z = b.Min.z;

            if (a.Max.x > b.Max.x)
                merged.Max.x = a.Max.x;
            else
                merged.Max.x = b.Max.x;
            if (a.Max.y > b.Max.y)
                merged.Max.y = a.Max.y;
            else
                merged.Max.y = b.Max.y;
            if (a.Max.z > b.Max.z)
                merged.Max.z = a.Max.z;
            else
                merged.Max.z = b.Max.z;
        }


        /// <summary>
        /// Creates a bounding box from a bounding sphere.
        /// </summary>
        /// <param name="boundingSphere">Bounding sphere to be used to create the bounding box.</param>
        /// <param name="boundingBox">Bounding box created from the bounding sphere.</param>
        public static void CreateFromSphere(ref FPBoundingSphere boundingSphere, out FPBoundingBox boundingBox)
        {
            boundingBox.Min.x = boundingSphere.Center.x - boundingSphere.Radius;
            boundingBox.Min.y = boundingSphere.Center.y - boundingSphere.Radius;
            boundingBox.Min.z = boundingSphere.Center.z - boundingSphere.Radius;

            boundingBox.Max.x = boundingSphere.Center.x + boundingSphere.Radius;
            boundingBox.Max.y = boundingSphere.Center.y + boundingSphere.Radius;
            boundingBox.Max.z = boundingSphere.Center.z + boundingSphere.Radius;
        }

    }
}
