using System;
using BEPUphysics.CollisionShapes.ConvexShapes;
 
using BEPUphysics.Settings;
using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.CollisionTests.CollisionAlgorithms
{
    ///<summary>
    /// Helper class to test spheres against each other.
    ///</summary>
    public static class SphereTester
    {
        /// <summary>
        /// Computes contact data for two spheres.
        /// </summary>
        /// <param name="a">First sphere.</param>
        /// <param name="b">Second sphere.</param>
        /// <param name="positionA">Position of the first sphere.</param>
        /// <param name="positionB">Position of the second sphere.</param>
        /// <param name="contact">Contact data between the spheres, if any.</param>
        /// <returns>Whether or not the spheres are touching.</returns>
        public static bool AreSpheresColliding(SphereShape a, SphereShape b, ref FPVector3 positionA, ref FPVector3 positionB, out ContactData contact)
        {
            contact = new ContactData();

            FP radiusSum = a.collisionMargin + b.collisionMargin;
            FPVector3 centerDifference;
            FPVector3.Subtract(ref positionB, ref positionA, out centerDifference);
            FP centerDistance = centerDifference.LengthSquared();

            if (centerDistance < (radiusSum + CollisionDetectionSettings.maximumContactDistance) * (radiusSum + CollisionDetectionSettings.maximumContactDistance))
            {
                //In collision!

                if (radiusSum > Toolbox.Epsilon) //This would be weird, but it is still possible to cause a NaN.
                    FPVector3.Multiply(ref centerDifference, a.collisionMargin / (radiusSum), out  contact.Position);
                else contact.Position = new FPVector3();
                FPVector3.Add(ref contact.Position, ref positionA, out contact.Position);

                centerDistance = FP.Sqrt(centerDistance);
                if (centerDistance > Toolbox.BigEpsilon)
                {
                    FPVector3.Divide(ref centerDifference, centerDistance, out contact.Normal);
                }
                else
                {
                    contact.Normal = Toolbox.UpVector;
                }
                contact.PenetrationDepth = radiusSum - centerDistance;

                return true;

            }
            return false;
        }
    }
}
