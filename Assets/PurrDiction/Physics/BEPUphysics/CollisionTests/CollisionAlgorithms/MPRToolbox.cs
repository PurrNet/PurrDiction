using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUutilities;
 
using System.Diagnostics;
using FixMath.NET;

namespace BEPUphysics.CollisionTests.CollisionAlgorithms
{
    /// <summary>
    /// Contains a variety of queries and computation methods that make use of minkowski portal refinement.
    /// </summary>
    public static class MPRToolbox
    {
        //TODO: Lots of nasty code repeat.
        //The common phases of portal construction, correction, and refinement are implemented with subtle differences.
        //With some effort, they could be shoved together.

        /// <summary>
        /// Number of iterations that the MPR system will run in its inner loop before giving up and returning with failure.
        /// </summary>
        public static int InnerIterationLimit = 15;
        /// <summary>
        /// Number of iterations that the MPR system will run in its outer loop before giving up and moving on to its inner loop.
        /// </summary>
        public static int OuterIterationLimit = 15;

        private static FP surfaceEpsilon = (FP)1e-7m;
        /// <summary>
        /// Gets or sets how close surface-finding based MPR methods have to get before exiting.
        /// Defaults to 1e-7.
        /// </summary>
        public static FP SurfaceEpsilon
        {
            get
            {
                return surfaceEpsilon;
            }
            set
            {
                if (value > F64.C0)
                    surfaceEpsilon = value;
                else throw new ArgumentException("Epsilon must be positive.");

            }
        }

        private static FP depthRefinementEpsilon = (FP)1e-4m;
        /// <summary>
        /// Gets or sets how close the penetration depth refinement system should converge before quitting.
        /// Making this smaller can help more precisely find a local minimum at the cost of performance.
        /// The change will likely only be visible on curved shapes, since polytopes will converge extremely rapidly to a precise local minimum.
        /// Defaults to 1e-4.
        /// </summary>
        public static FP DepthRefinementEpsilon
        {
            get
            {
                return depthRefinementEpsilon;
            }
            set
            {
                if (value > F64.C0)
                    depthRefinementEpsilon = value;
                else throw new ArgumentException("Epsilon must be positive.");

            }
        }

        private static FP rayCastSurfaceEpsilon = (FP)1e-9m;
        /// <summary>
        /// Gets or sets how close surface-finding ray casts have to get before exiting.
        /// Defaults to 1e-9.
        /// </summary>
        public static FP RayCastSurfaceEpsilon
        {
            get
            {
                return rayCastSurfaceEpsilon;
            }
            set
            {
                if (value > F64.C0)
                    rayCastSurfaceEpsilon = value;
                else
                    throw new ArgumentException("Epsilon must be positive.");
            }
        }

        private static int maximumDepthRefinementIterations = 3;
        /// <summary>
        /// Gets or sets the maximum number of iterations to use to reach the local penetration depth minimum when using the RefinePenetration function.
        /// Increasing this allows the system to work longer to find local penetration minima.
        /// The change will likely only be visible on curved shapes, since polytopes will converge extremely rapidly to a precise local minimum.
        /// Defaults to 3.
        /// </summary>
        public static int MaximumDepthRefinementIterations
        {
            get
            {
                return maximumDepthRefinementIterations;
            }
            set
            {
                if (value > 0)
                    maximumDepthRefinementIterations = value;
                else throw new ArgumentException("Iteration count must be positive.");
            }
        }

        /// <summary>
        /// Gets a world space point in the overlapped volume between two shapes.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="transformA">Transformation to apply to the first shape.</param>
        /// <param name="transformB">Transformation to apply to the second shape.</param>
        /// <param name="position">Position within the overlapped volume of the two shapes, if any.</param>
        /// <returns>Whether or not the two shapes overlap.</returns>
        public static bool GetOverlapPosition(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform transformA, ref RigidTransform transformB, out FPVector3 position)
        {
            RigidTransform localTransformB;
            MinkowskiToolbox.GetLocalTransform(ref transformA, ref transformB, out localTransformB);
            bool toReturn = GetLocalOverlapPosition(shapeA, shapeB, ref localTransformB, out position);
            RigidTransform.Transform(ref position, ref transformA, out position);
            return toReturn;

        }


        /// <summary>
        /// Gets a point in the overlapped volume between two shapes in shape A's local space.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="localTransformB">Transform of shapeB within the local space of A.</param>
        /// <param name="position">Position within the overlapped volume of the two shapes in shape A's local space, if any.</param>
        /// <returns>Whether or not the two shapes overlap.</returns>
        public static bool GetLocalOverlapPosition(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, out FPVector3 position)
        {
            return GetLocalOverlapPosition(shapeA, shapeB, ref localTransformB.Position, ref localTransformB, out position);
        }

        internal static bool GetLocalOverlapPosition(ConvexShape shapeA, ConvexShape shapeB, ref FPVector3 originRay, ref RigidTransform localTransformB, out FPVector3 position)
        {
            //Compute the origin ray.  This points from a point known to be inside the minkowski sum to the origin.
            //The centers of the shapes are used to create the interior point.

            //It's possible that the two objects' centers are overlapping, or very very close to it.  In this case, 
            //they are obviously colliding and we can immediately exit.
            if (originRay.LengthSquared() < Toolbox.Epsilon)
            {
                position = new FPVector3();
                //DEBUGlastPosition = position;
                return true;
            }

            FPVector3 v0;
            FPVector3.Negate(ref originRay, out v0); //Since we're in A's local space, A-B is just -B.



            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = originRay;
            FPVector3 v1;
            FPVector3 v1A, v1B; //extreme point contributions from each shape.  Used later to compute contact position; could be used to cache simplex too.
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v1A, out v1B, out v1);

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3 v2A, v2B;
            FPVector3.Cross(ref v1, ref v0, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!
                //If the origin is further along this direction than the extreme point, then there is no intersection.
                //If the origin is within this extreme point, then there is an intersection.
                FP dot;
                FPVector3.Dot(ref v1, ref originRay, out dot);
                if (dot < F64.C0)
                {
                    //Origin is outside.
                    position = new FPVector3();
                    return false;
                }
                //Origin is inside.
                //Compute barycentric coordinates along simplex (segment).
                FP dotv0;
                //Dot > 0, so dotv0 starts out negative.
                FPVector3.Dot(ref v0, ref originRay, out dotv0);
                FP barycentricCoordinate = -dotv0 / (dot - dotv0);
                //Vector3.Subtract(ref v1A, ref v0A, out offset); //'v0a' is just the zero vector, so there's no need to calculate the offset.
                FPVector3.Multiply(ref v1A, barycentricCoordinate, out position);
                return true;
            }
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v2A, out v2B, out v2);

            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Subtract(ref v1, ref v0, out temp1);
            FPVector3.Subtract(ref v2, ref v0, out temp2);
            FPVector3.Cross(ref temp1, ref temp2, out n);


            FPVector3 v3A, v3B, v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v3A, out v3B, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                    break;
                count++;
                //By now, the simplex is a tetrahedron, but it is not known whether or not the origin ray found earlier actually passes through the portal
                //defined by v1, v2, v3.

                // If the origin is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FP dot;
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    v2A = v3A;
                    v2B = v3B;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }

                // If the origin is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    v1A = v3A;
                    v1B = v3B;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v2, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            while (true)
            {
                //Test the origin against the plane defined by v1, v2, v3.  If it's inside, we're done.
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v3, ref v2, out temp1);
                FPVector3.Subtract(ref v1, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);
                FP dot;
                FPVector3.Dot(ref n, ref v1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3 temp3;
                    //Compute the barycentric coordinates of the origin.
                    //This is done by computing the scaled volume (parallelepiped) of the tetrahedra 
                    //formed by each triangle of the v0v1v2v3 tetrahedron and the origin.

                    //TODO: consider a different approach using T parameter or something.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v2, ref v0, out temp2);
                    FPVector3.Subtract(ref v3, ref v0, out temp3);

                    FPVector3 cross;
                    FPVector3.Cross(ref temp1, ref temp2, out cross);
                    FP v0v1v2v3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0v1v2v3volume);

                    FPVector3.Cross(ref v1, ref v2, out cross);
                    FP ov1v2v3volume;
                    FPVector3.Dot(ref cross, ref v3, out ov1v2v3volume);

                    FPVector3.Cross(ref originRay, ref temp2, out cross);
                    FP v0ov2v3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0ov2v3volume);

                    FPVector3.Cross(ref temp1, ref originRay, out cross);
                    FP v0v1ov3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0v1ov3volume);

                    if (v0v1v2v3volume > Toolbox.Epsilon * F64.C0p01)
                    {
                        FP inverseTotalVolume = F64.C1 / v0v1v2v3volume;
                        FP v0Weight = ov1v2v3volume * inverseTotalVolume;
                        FP v1Weight = v0ov2v3volume * inverseTotalVolume;
                        FP v2Weight = v0v1ov3volume * inverseTotalVolume;
                        FP v3Weight = F64.C1 - v0Weight - v1Weight - v2Weight;
                        position = v1Weight * v1A + v2Weight * v2A + v3Weight * v3A;
                    }
                    else
                    {
                        position = new FPVector3();
                    }
                    //DEBUGlastPosition = position;
                    return true;
                }

                //We haven't yet found the origin.  Find the support point in the portal's outward facing direction.
                FPVector3 v4, v4A, v4B;
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v4A, out v4B, out v4);
                //If the origin is further along the direction than the extreme point, it's not inside the shape.
                FP dot2;
                FPVector3.Dot(ref v4, ref n, out dot2);
                if (dot2 < F64.C0)
                {
                    //The origin is outside!
                    position = new FPVector3();
                    return false;
                }

                //If the plane which generated the normal is very close to the extreme point, then we're at the surface
                //and we have not found the origin; it's either just BARELY inside, or it is outside.  Assume it's outside.
                if (dot2 - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    position = new FPVector3();
                    //DEBUGlastPosition = position;
                    return false;
                }
                count++;

                //Still haven't exited, so refine the portal.
                //Test origin against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)
                FPVector3.Cross(ref v4, ref v0, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                        v1A = v4A;
                        v1B = v4B;
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                        v3A = v4A;
                        v3B = v4B;
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                        v2A = v4A;
                        v2B = v4B;
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                        v1A = v4A;
                        v1B = v4B;
                    }
                }

                //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
                //    Debug.WriteLine("Break.");

            }


        }



        /// <summary>
        /// Determines if two shapes are colliding.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape of the pair.</param>
        /// <param name="transformA">Transformation to apply to shape A.</param>
        /// <param name="transformB">Transformation to apply to shape B.</param>
        /// <returns>Whether or not the shapes are overlapping.</returns>
        public static bool AreShapesOverlapping(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform transformA, ref RigidTransform transformB)
        {
            RigidTransform localTransformB;
            MinkowskiToolbox.GetLocalTransform(ref transformA, ref transformB, out localTransformB);
            return AreLocalShapesOverlapping(shapeA, shapeB, ref localTransformB);

        }

        /// <summary>
        /// Determines if two shapes are colliding.  Shape B is positioned relative to shape A.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape of the pair.</param>
        /// <param name="localTransformB">Relative transform of shape B to shape A.</param>
        /// <returns>Whether or not the shapes are overlapping.</returns>
        public static bool AreLocalShapesOverlapping(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB)
        {
            return AreLocalShapesOverlapping(shapeA, shapeB, ref localTransformB.Position, ref localTransformB);
        }

        /// <summary>
        /// Determines if two shapes are colliding.  Shape B is positioned relative to shape A.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape of the pair.</param>
        /// <param name="originRay">Direction in which to cast the overlap ray.  Necessary when an object's origin is not contained in its geometry.</param>
        /// <param name="localTransformB">Relative transform of shape B to shape A.</param>
        /// <returns>Whether or not the shapes are overlapping.</returns>
        internal static bool AreLocalShapesOverlapping(ConvexShape shapeA, ConvexShape shapeB, ref FPVector3 originRay, ref RigidTransform localTransformB)
        {
            //Compute the origin ray.  This points from a point known to be inside the minkowski sum to the origin.
            //The centers of the shapes are used to create the interior point.

            //It's possible that the two objects' centers are overlapping, or very very close to it.  In this case, 
            //they are obviously colliding and we can immediately exit.
            if (originRay.LengthSquared() < Toolbox.Epsilon)
            {
                return true;
            }

            FPVector3 v0;
            FPVector3.Negate(ref originRay, out v0); //Since we're in A's local space, A-B is just -B.



            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = originRay;
            FPVector3 v1;
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v1);

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3.Cross(ref v1, ref v0, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!
                //If the origin is further along this direction than the extreme point, then there is no intersection.
                //If the origin is within this extreme point, then there is an intersection.
                FP dot;
                FPVector3.Dot(ref v1, ref originRay, out dot);
                if (dot < F64.C0)
                {
                    //Origin is outside.
                    return false;
                }
                //Origin is inside.
                return true;
            }
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v2);

            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Subtract(ref v1, ref v0, out temp1);
            FPVector3.Subtract(ref v2, ref v0, out temp2);
            FPVector3.Cross(ref temp1, ref temp2, out n);


            FPVector3 v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                    break;
                count++;
                //By now, the simplex is a tetrahedron, but it is not known whether or not the origin ray found earlier actually passes through the portal
                //defined by v1, v2, v3.

                // If the origin is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FP dot;
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }

                // If the origin is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v2, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            while (true)
            {
                //Test the origin against the plane defined by v1, v2, v3.  If it's inside, we're done.
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v3, ref v2, out temp1);
                FPVector3.Subtract(ref v1, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);
                FP dot;
                FPVector3.Dot(ref n, ref v1, out dot);
                if (dot >= F64.C0)
                {
                    return true;
                }

                //We haven't yet found the origin.  Find the support point in the portal's outward facing direction.
                FPVector3 v4;
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v4);
                //If the origin is further along the direction than the extreme point, it's not inside the shape.
                FP dot2;
                FPVector3.Dot(ref v4, ref n, out dot2);
                if (dot2 < F64.C0)
                {
                    //The origin is outside!
                    return false;
                }

                //If the plane which generated the normal is very close to the extreme point, then we're at the surface
                //and we have not found the origin; it's either just BARELY inside, or it is outside.  Assume it's outside.
                if (dot2 - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    return false;
                }
                count++;

                //Still haven't exited, so refine the portal.
                //Test origin against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)
                FPVector3.Cross(ref v4, ref v0, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                    }
                }

                //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
                //    Debug.WriteLine("Break.");

            }


        }

        /// <summary>
        /// Casts a ray from the origin in the given direction at the surface of the minkowski difference.
        /// Assumes that the origin is within the minkowski difference.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="localTransformB">Transformation of shape B relative to shape A.</param>
        /// <param name="direction">Direction to cast the ray.</param>
        /// <param name="t">Length along the direction vector that the impact was found.</param>
        /// <param name="normal">Normal of the impact at the surface of the convex.</param>
        public static void LocalSurfaceCast(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, ref FPVector3 direction, out FP t, out FPVector3 normal)
        {
            // Local surface cast is very similar to regular MPR.  However, instead of starting at an interior point and targeting the origin,
            // the ray starts at the origin (a point known to be in both shape and shapeB), and just goes towards the direction until the surface
            // is found.  The portal (v1, v2, v3) at termination defines the surface normal, and the distance from the origin to the portal along the direction is used as the 't' result.


            //'v0' is no longer explicitly tracked since it is simply the origin.


            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = direction;
            FPVector3 v1;
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v1);
            //v1 could be zero in some degenerate cases.
            //if (v1.LengthSquared() < Toolbox.Epsilon)
            //{
            //    t = 0;
            //    normal = n;
            //    return;
            //}

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3.Cross(ref direction, ref v1, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!

                FP rayLengthSquared = direction.LengthSquared();
                if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
                    FPVector3.Divide(ref direction, FP.Sqrt(rayLengthSquared), out normal);
                else
                    normal = new FPVector3();

                FP rate;
                FPVector3.Dot(ref  normal, ref direction, out rate);
                FP distance;
                FPVector3.Dot(ref  normal, ref v1, out distance);
                if (rate > F64.C0)
                    t = distance / rate;
                else
                    t = F64.C0;
                return;
            }
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v2);




            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Cross(ref v1, ref v2, out n);

            //It's possible that v1 and v2 were constructed in such a way that 'n' is not properly calibrated
            //relative to the direction vector.
            FP dot;
            FPVector3.Dot(ref n, ref direction, out dot);
            if (dot > F64.C0)
            {
                //It's not properly calibrated.  Flip the winding (and the previously calculated normal).
                FPVector3.Negate(ref n, out n);
                temp1 = v1;
                v1 = v2;
                v2 = temp1;
            }

            FPVector3 v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                {
                    //Can't enclose the origin! That's a bit odd; something is wrong.
                    t = FP.MaxValue;
                    normal = Toolbox.UpVector;
                    return;
                }
                count++;

                //By now, the simplex is a tetrahedron, but it is not known whether or not the ray actually passes through the portal
                //defined by v1, v2, v3.

                // If the direction is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FPVector3.Dot(ref temp1, ref direction, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v1, ref v3, out n);
                    continue;
                }

                // If the direction is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref direction, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v2, ref v3, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref Toolbox.ZeroVector, ref v1, ref v2, ref v3, ref direction))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            count = 0;
            while (true)
            {
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v1, ref v2, out temp1);
                FPVector3.Subtract(ref v3, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);


                //Keep working towards the surface.  Find the next extreme point.
                FPVector3 v4;
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v4);


                //If the plane which generated the normal is very close to the extreme point, then we're at the surface.
                FPVector3.Dot(ref n, ref v1, out dot);
                FP supportDot;
                FPVector3.Dot(ref v4, ref n, out supportDot);

                if (supportDot - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //normal = n;
                    //Fix64 normalLengthInverse = 1 / normal.Length();
                    //Vector3.Multiply(ref normal, normalLengthInverse, out normal);
                    ////Find the distance from the origin to the plane.
                    //t = dot * normalLengthInverse;

                    FP lengthSquared = n.LengthSquared();
                    if (lengthSquared > Toolbox.Epsilon * F64.C0p01)
                    {
                        FPVector3.Divide(ref n, FP.Sqrt(lengthSquared), out normal);

                        //The plane is very close to the surface, and the ray is known to pass through it.
                        //dot is the rate.
                        FPVector3.Dot(ref normal, ref direction, out dot);
                        //supportDot is the distance to the plane.
                        FPVector3.Dot(ref normal, ref v1, out supportDot);
                        if (dot > F64.C0)
                            t = supportDot / dot;
                        else
                            t = F64.C0;
                    }
                    else
                    {
                        normal = FPVector3.up;
                        t = F64.C0;
                    }
                    ////DEBUG STUFF:

                    //DEBUGlastRayT = t;
                    //DEBUGlastRayDirection = direction;
                    //DEBUGlastDepth = t;
                    //DEBUGlastNormal = normal;
                    //DEBUGlastV1 = v1;
                    //DEBUGlastV2 = v2;
                    //DEBUGlastV3 = v3;
                    return;
                }

                //Still haven't exited, so refine the portal.
                //Test direction against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)



                //This may look a little weird at first.
                //'inside' here means 'on the positive side of the plane.'
                //There are three total planes being tested, one for each of v1, v2, and v3.
                //The planes are created from consistently wound vertices, so it's possible to determine
                //where the ray passes through the portal based upon its relationship to two of the three planes.
                //The third vertex which is found to be opposite the face which contains the ray is replaced with the extreme point.

                //This v4 x direction is just a minor reordering of a scalar triple product: (v1 x v4) * direction.
                //It eliminates the need for extra cross products for the inner if.
                FPVector3.Cross(ref v4, ref direction, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                    }
                }

                count++;

                //Here's an unoptimized equivalent without the scalar triple product reorder.
                #region Equivalent refinement
                //Vector3.Cross(ref v1, ref v4, out temp1);
                //Vector3.Dot(ref temp1, ref direction, out dot);
                //if (dot > 0)
                //{
                //    Vector3.Cross(ref v2, ref v4, out temp2);
                //    Vector3.Dot(ref temp2, ref direction, out dot);
                //    if (dot > 0)
                //    {
                //        //Inside v1, v4, v0 and inside v2, v4, v0
                //        v1 = v4;
                //    }
                //    else
                //    {
                //        //Inside v1, v4, v0 and outside v2, v4, v0
                //        v3 = v4;
                //    }
                //}
                //else
                //{
                //    Vector3.Cross(ref v3, ref v4, out temp2);
                //    Vector3.Dot(ref temp2, ref direction, out dot);
                //    if (dot > 0)
                //    {
                //        //Outside v1, v4, v0 and inside v3, v4, v0
                //        v2 = v4;
                //    }
                //    else
                //    {
                //        //Outside v1, v4, v0 and outside v3, v4, v0
                //        v1 = v4;
                //    }
                //}
                #endregion

                //if (!VerifySimplex(ref Toolbox.ZeroVector, ref v1, ref v2, ref v3, ref direction))
                //    Debug.WriteLine("Break.");
            }
        }

        /// <summary>
        /// Casts a ray from the origin in the given direction at the surface of the minkowski difference.
        /// Assumes that the origin is within the minkowski difference.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="localTransformB">Transformation of shape B relative to shape A.</param>
        /// <param name="direction">Direction to cast the ray.</param>
        /// <param name="t">Length along the direction vector that the impact was found.</param>
        /// <param name="normal">Normal of the impact at the surface of the convex.</param>
        /// <param name="position">Location of the ray cast hit on the surface of A.</param>
        public static void LocalSurfaceCast(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, ref FPVector3 direction, out FP t, out FPVector3 normal, out FPVector3 position)
        {
            // Local surface cast is very similar to regular MPR.  However, instead of starting at an interior point and targeting the origin,
            // the ray starts at the origin (a point known to be in both shape and shapeB), and just goes towards the direction until the surface
            // is found.  The portal (v1, v2, v3) at termination defines the surface normal, and the distance from the origin to the portal along the direction is used as the 't' result.


            //'v0' is no longer explicitly tracked since it is simply the origin.


            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = direction;
            FPVector3 v1, v1A;
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v1A, out v1);
            //v1 could be zero in some degenerate cases.
            //if (v1.LengthSquared() < Toolbox.Epsilon)
            //{
            //    t = 0;
            //    normal = n;
            //    return;
            //}

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2, v2A;
            FPVector3.Cross(ref direction, ref v1, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!

                FP rayLengthSquared = direction.LengthSquared();
                if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
                    FPVector3.Divide(ref direction, FP.Sqrt(rayLengthSquared), out normal);
                else
                    normal = new FPVector3();

                FP rate;
                FPVector3.Dot(ref  normal, ref direction, out rate);
                FP distance;
                FPVector3.Dot(ref  normal, ref v1, out distance);
                if (rate > F64.C0)
                    t = distance / rate;
                else
                    t = F64.C0;
                position = v1A;
                return;
            }
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v2A, out v2);




            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Cross(ref v1, ref v2, out n);

            //It's possible that v1 and v2 were constructed in such a way that 'n' is not properly calibrated
            //relative to the direction vector.
            FP dot;
            FPVector3.Dot(ref n, ref direction, out dot);
            if (dot > F64.C0)
            {
                //It's not properly calibrated.  Flip the winding (and the previously calculated normal).
                FPVector3.Negate(ref n, out n);
                temp1 = v1;
                v1 = v2;
                v2 = temp1;

                temp1 = v1A;
                v1A = v2A;
                v2A = temp1;
            }

            FPVector3 v3, v3A;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v3A, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                {
                    //Can't enclose the origin! That's a bit odd; something is wrong.
                    t = FP.MaxValue;
                    normal = Toolbox.UpVector;
                    position = new FPVector3();
                    return;
                }
                count++;

                //By now, the simplex is a tetrahedron, but it is not known whether or not the ray actually passes through the portal
                //defined by v1, v2, v3.

                // If the direction is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FPVector3.Dot(ref temp1, ref direction, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    v2A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v1, ref v3, out n);
                    continue;
                }

                // If the direction is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref direction, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    v1A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v2, ref v3, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref Toolbox.ZeroVector, ref v1, ref v2, ref v3, ref direction))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            count = 0;
            while (true)
            {
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v1, ref v2, out temp1);
                FPVector3.Subtract(ref v3, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);


                //Keep working towards the surface.  Find the next extreme point.
                FPVector3 v4, v4A;
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v4A, out v4);


                //If the plane which generated the normal is very close to the extreme point, then we're at the surface.
                FPVector3.Dot(ref n, ref v1, out dot);
                FP supportDot;
                FPVector3.Dot(ref v4, ref n, out supportDot);

                if (supportDot - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //normal = n;
                    //Fix64 normalLengthInverse = 1 / normal.Length();
                    //Vector3.Multiply(ref normal, normalLengthInverse, out normal);
                    ////Find the distance from the origin to the plane.
                    //t = dot * normalLengthInverse;

                    FP lengthSquared = n.LengthSquared();
                    if (lengthSquared > Toolbox.Epsilon * F64.C0p01)
                    {
                        FPVector3.Divide(ref n, FP.Sqrt(lengthSquared), out normal);

                        //The plane is very close to the surface, and the ray is known to pass through it.
                        //dot is the rate.
                        FPVector3.Dot(ref normal, ref direction, out dot);
                        //supportDot is the distance to the plane.
                        FPVector3.Dot(ref normal, ref v1, out supportDot);
                        if (dot > F64.C0)
                            t = supportDot / dot;
                        else
                            t = F64.C0;
                    }
                    else
                    {
                        normal = FPVector3.up;
                        t = F64.C0;
                    }

                    FP v1Weight, v2Weight, v3Weight;
                    FPVector3.Multiply(ref direction, t, out position);

                    Toolbox.GetBarycentricCoordinates(ref position, ref v1, ref v2, ref v3, out v1Weight, out v2Weight, out v3Weight);
                    FPVector3.Multiply(ref v1A, v1Weight, out position);
                    FPVector3 temp;
                    FPVector3.Multiply(ref v2A, v2Weight, out temp);
                    FPVector3.Add(ref temp, ref position, out position);
                    FPVector3.Multiply(ref v3A, v3Weight, out temp);
                    FPVector3.Add(ref temp, ref position, out position);
                    ////DEBUG STUFF:

                    //DEBUGlastRayT = t;
                    //DEBUGlastRayDirection = direction;
                    //DEBUGlastDepth = t;
                    //DEBUGlastNormal = normal;
                    //DEBUGlastV1 = v1;
                    //DEBUGlastV2 = v2;
                    //DEBUGlastV3 = v3;
                    return;
                }

                //Still haven't exited, so refine the portal.
                //Test direction against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)



                //This may look a little weird at first.
                //'inside' here means 'on the positive side of the plane.'
                //There are three total planes being tested, one for each of v1, v2, and v3.
                //The planes are created from consistently wound vertices, so it's possible to determine
                //where the ray passes through the portal based upon its relationship to two of the three planes.
                //The third vertex which is found to be opposite the face which contains the ray is replaced with the extreme point.

                //This v4 x direction is just a minor reordering of a scalar triple product: (v1 x v4) * direction.
                //It eliminates the need for extra cross products for the inner if.
                FPVector3.Cross(ref v4, ref direction, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                        v1A = v4A;
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                        v3A = v4A;
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                        v2A = v4A;
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                        v1A = v4A;
                    }
                }

                count++;

                //Here's an unoptimized equivalent without the scalar triple product reorder.
                #region Equivalent refinement
                //Vector3.Cross(ref v1, ref v4, out temp1);
                //Vector3.Dot(ref temp1, ref direction, out dot);
                //if (dot > 0)
                //{
                //    Vector3.Cross(ref v2, ref v4, out temp2);
                //    Vector3.Dot(ref temp2, ref direction, out dot);
                //    if (dot > 0)
                //    {
                //        //Inside v1, v4, v0 and inside v2, v4, v0
                //        v1 = v4;
                //    }
                //    else
                //    {
                //        //Inside v1, v4, v0 and outside v2, v4, v0
                //        v3 = v4;
                //    }
                //}
                //else
                //{
                //    Vector3.Cross(ref v3, ref v4, out temp2);
                //    Vector3.Dot(ref temp2, ref direction, out dot);
                //    if (dot > 0)
                //    {
                //        //Outside v1, v4, v0 and inside v3, v4, v0
                //        v2 = v4;
                //    }
                //    else
                //    {
                //        //Outside v1, v4, v0 and outside v3, v4, v0
                //        v1 = v4;
                //    }
                //}
                #endregion

                //if (!VerifySimplex(ref Toolbox.ZeroVector, ref v1, ref v2, ref v3, ref direction))
                //    Debug.WriteLine("Break.");
            }
        }

        static bool VerifySimplex(ref FPVector3 v0, ref FPVector3 v1, ref FPVector3 v2, ref FPVector3 v3, ref FPVector3 direction)
        {


            //v1, v0, v3
            FPVector3 cross = FPVector3.Cross(v0 - v1, v3 - v1);
            FP planeProduct1 = FPVector3.Dot(cross, direction);
            //v3, v0, v2
            cross = FPVector3.Cross(v0 - v3, v2 - v3);
            FP planeProduct2 = FPVector3.Dot(cross, direction);
            //v2, v0, v1
            cross = FPVector3.Cross(v0 - v2, v1 - v2);
            FP planeProduct3 = FPVector3.Dot(cross, direction);
            return (planeProduct1 <= F64.C0 && planeProduct2 <= F64.C0 && planeProduct3 <= F64.C0) ||
                (planeProduct1 >= F64.C0 && planeProduct2 >= F64.C0 && planeProduct3 >= F64.C0);
        }

        /// <summary>
        /// Gets a contact point between two convex shapes.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="transformA">Transformation to apply to the first shape.</param>
        /// <param name="transformB">Transformation to apply to the second shape.</param>
        /// <param name="penetrationAxis">Axis along which to first test the penetration depth.</param>
        /// <param name="contact">Contact data between the two shapes, if any.</param>
        /// <returns>Whether or not the shapes overlap.</returns>
        public static bool GetContact(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform transformA, ref RigidTransform transformB, ref FPVector3 penetrationAxis, out ContactData contact)
        {
            RigidTransform localTransformB;
            MinkowskiToolbox.GetLocalTransform(ref transformA, ref transformB, out localTransformB);
            if (MPRToolbox.AreLocalShapesOverlapping(shapeA, shapeB, ref localTransformB))
            {
                //First, try to use the heuristically found direction.  This comes from either the GJK shallow contact separating axis or from the relative velocity.
                FPVector3 rayCastDirection;
                FP lengthSquared = penetrationAxis.LengthSquared();
                if (lengthSquared > Toolbox.Epsilon)
                {
                    FPVector3.Divide(ref penetrationAxis, FP.Sqrt(lengthSquared), out rayCastDirection);// (Vector3.Normalize(localDirection) + Vector3.Normalize(collidableB.worldTransform.Position - collidableA.worldTransform.Position)) / 2;
                    MPRToolbox.LocalSurfaceCast(shapeA, shapeB, ref localTransformB, ref rayCastDirection, out contact.PenetrationDepth, out contact.Normal);
                }
                else
                {
                    contact.PenetrationDepth = FP.MaxValue;
                    contact.Normal = Toolbox.UpVector;
                }
                //Try the offset between the origins as a second option.  Sometimes this is a better choice than the relative velocity.
                //TODO: Could use the position-finding MPR iteration to find the A-B direction hit by continuing even after the origin has been found (optimization).
                FPVector3 normalCandidate;
                FP depthCandidate;
                lengthSquared = localTransformB.Position.LengthSquared();
                if (lengthSquared > Toolbox.Epsilon)
                {
                    FPVector3.Divide(ref localTransformB.Position, FP.Sqrt(lengthSquared), out rayCastDirection);
                    MPRToolbox.LocalSurfaceCast(shapeA, shapeB, ref localTransformB, ref rayCastDirection, out depthCandidate, out normalCandidate);
                    if (depthCandidate < contact.PenetrationDepth)
                    {
                        contact.Normal = normalCandidate;
                        contact.PenetrationDepth = depthCandidate;
                    }
                }

                //if (contact.PenetrationDepth > 1)
                //    Debug.WriteLine("Break.");

                //Correct the penetration depth.
                RefinePenetration(shapeA, shapeB, ref localTransformB, contact.PenetrationDepth, ref contact.Normal, out contact.PenetrationDepth, out contact.Normal, out contact.Position);

                ////Correct the penetration depth.
                //MPRTesting.LocalSurfaceCast(shape, shapeB, ref localPoint, ref contact.Normal, out contact.PenetrationDepth, out rayCastDirection);


                ////The local casting can optionally continue.  Eventually, it will converge to the local minimum.
                //while (true)
                //{
                //    MPRTesting.LocalSurfaceCast(collidableA.Shape, collidableB.Shape, ref localPoint, ref contact.Normal, out depthCandidate, out normalCandidate);
                //    if (contact.PenetrationDepth - depthCandidate <= Toolbox.BigEpsilon)
                //        break;

                //    contact.PenetrationDepth = depthCandidate;
                //    contact.Normal = normalCandidate;
                //}

                contact.Id = -1;
                //we're still in local space! transform it all back.
                Matrix3x3 orientation;
                Matrix3x3.CreateFromQuaternion(ref transformA.Orientation, out orientation);
                Matrix3x3.Transform(ref contact.Normal, ref orientation, out contact.Normal);
                //Vector3.Negate(ref contact.Normal, out contact.Normal);
                Matrix3x3.Transform(ref contact.Position, ref orientation, out contact.Position);
                FPVector3.Add(ref contact.Position, ref transformA.Position, out contact.Position);
                return true;
            }
            contact = new ContactData();
            return false;
        }

        /// <summary>
        /// Incrementally refines the penetration depth and normal towards the local minimum.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="localTransformB">Transformation of shape B relative to shape A.</param>
        /// <param name="initialDepth">Initial depth estimate.</param>
        /// <param name="initialNormal">Initial normal estimate.</param>
        /// <param name="penetrationDepth">Refined penetration depth.</param>
        /// <param name="refinedNormal">Refined normal.</param>
        /// <param name="position">Refined position.</param>
        public static void RefinePenetration(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, FP initialDepth, ref FPVector3 initialNormal, out FP penetrationDepth, out FPVector3 refinedNormal, out FPVector3 position)
        {
            //The local casting can optionally continue.  Eventually, it will converge to the local minimum.
            int optimizingCount = 0;
            refinedNormal = initialNormal;
            penetrationDepth = initialDepth;
            FP candidateDepth;
            FPVector3 candidateNormal;

            while (true)
            {

                MPRToolbox.LocalSurfaceCast(shapeA, shapeB, ref localTransformB, ref refinedNormal, out candidateDepth, out candidateNormal, out position);
                if (penetrationDepth - candidateDepth <= depthRefinementEpsilon ||
                    ++optimizingCount >= maximumDepthRefinementIterations)
                {
                    //If we've reached the end due to convergence, the normal will be extremely close to correct (if not 100% correct).
                    //The candidateDepth computed is the previous contact normal's depth.
                    //The reason why the previous normal is kept is that the last raycast computed the depth for that normal, not the new normal.
                    penetrationDepth = candidateDepth;
                    break;
                }

                penetrationDepth = candidateDepth;
                refinedNormal = candidateNormal;

            }
        }

        //SWEEPS

        /// <summary>
        /// Sweeps the shapes against each other and finds a point, time, and normal of impact.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="sweepA">Sweep direction and amount to apply to the first shape.</param>
        /// <param name="sweepB">Sweep direction and amount to apply to the second shape.</param>
        /// <param name="transformA">Initial transform to apply to the first shape.</param>
        /// <param name="transformB">Initial transform to apply to the second shape.</param>
        /// <param name="hit">Hit data between the two shapes, if any.</param>
        /// <returns>Whether or not the swept shapes hit each other.</returns>
        public static bool Sweep(ConvexShape shapeA, ConvexShape shapeB, ref FPVector3 sweepA, ref FPVector3 sweepB, ref RigidTransform transformA, ref RigidTransform transformB, out FPRayHit hit)
        {
            //Put the relative velocity into shape's local space.
            FPVector3 velocityWorld;
            //note the order of subtraction.  It 'should' be B-A, but the ray direction the algorithm works with is actually OPPOSITE.
            FPVector3.Subtract(ref sweepA, ref sweepB, out velocityWorld);
            FPQuaternion conjugateOrientationA;
            FPQuaternion.Conjugate(ref transformA.Orientation, out conjugateOrientationA);
            FPVector3 localDirection;
            FPQuaternion.Transform(ref velocityWorld, ref conjugateOrientationA, out localDirection);


            //Sweeping two objects against each other is very similar to the local surface cast.
            //The ray starts at the origin and goes in the sweep direction.
            //However, unlike the local surface cast, the origin may start outside of the minkowski difference.
            //Additionally, the method can early out if the length traversed by the ray is found to be longer than the maximum length, or if the origin is found to be outside the minkowski difference.

            //The support points of the minkowski difference are also modified.  By default, the minkowski difference should very rarely contain the origin.
            //Sweep tests aren't very useful if the objects are intersecting!
            //However, in order for the local surface cast to actually find a proper result (assuming there is a hit at all), the origin must be inside the minkowski difference.
            //So, expand the minkowski difference using the sweep direction with magnitude sufficient to fully include the plane defined by the origin and the sweep direction.
            //If there's going to be a hit, then the origin will be within this expanded shape.



            //If the sweep direction is found to be negative, the ray can be thought of as pointing away from the shape.
            //However, the minkowski difference may contain the shape.  In this case, the time of impact is zero.

            //If the swept (with sweep = 0 in case of incorrect direction) minkowski difference does not contain the shape, then the raycast cannot begin and we also know that the shapes will not intersect.

            //If the sweep amount is nonnegative and the minkowski difference contains the shape, then the normal raycasting process can continue.
            //Perform the usual local raycast, but use the swept minkowski difference.
            //Once the surface is found, the final t parameter of the ray is equal to the sweep distance minus the local raycast computed t parameter.



            RigidTransform localTransformB;
            MinkowskiToolbox.GetLocalTransform(ref transformA, ref transformB, out localTransformB);


            //First: Compute the sweep amount along the sweep direction.
            //This sweep amount needs to expand the minkowski difference to fully intersect the plane defined by the sweep direction and origin.

            FP rayLengthSquared = localDirection.LengthSquared();
            FP sweepLength;
            if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
            {
                FPVector3.Dot(ref localTransformB.Position, ref localDirection, out sweepLength);
                sweepLength /= rayLengthSquared;
                //Scale the sweep length by the margins.  Divide by the length to pull the margin into terms of the length of the ray.
                sweepLength += (shapeA.MaximumRadius + shapeB.MaximumRadius) / FP.Sqrt(rayLengthSquared);
            }
            else
            {
                rayLengthSquared = F64.C0;
                sweepLength = F64.C0;
            }
            //If the sweep direction is found to be negative, the ray can be thought of as pointing away from the shape.
            //Do not sweep backward.
            bool negativeLength = sweepLength < F64.C0;
            if (negativeLength)
                sweepLength = F64.C0;



            FPVector3 sweep;
            FPVector3.Multiply(ref localDirection, sweepLength, out sweep);
            //Check to see if the origin is contained within the swept shape.
            if (!AreSweptShapesIntersecting(shapeA, shapeB, ref sweep, ref localTransformB, out hit.Location)) //Computes a hit location to be used if the early-outs due to being in contact.
            {
                //The origin is not contained within the sweep volume.  The raycast definitely misses.
                hit.T = FP.MaxValue;
                hit.Normal = new FPVector3();
                hit.Location = new FPVector3();
                return false;
            }
            if (negativeLength)
            {
                //The origin is contained, but we shouldn't continue.
                //The ray is facing backwards.  The time of impact would be 0.
                hit.T = F64.C0;
                FPVector3.Normalize(ref localDirection, out hit.Normal);
                FPQuaternion.Transform(ref hit.Normal, ref transformA.Orientation, out hit.Normal);
                //hit.Location = hit.T * localDirection;
                FPQuaternion.Transform(ref hit.Location, ref transformA.Orientation, out hit.Location);
                FPVector3.Add(ref hit.Location, ref transformA.Position, out hit.Location);
                hit.Location += sweepA * hit.T;
                return true;
            }

            //OKAY! We've finally finished all the pre-testing.  Cast the ray!
            if (LocalSweepCast(shapeA, shapeB, sweepLength, rayLengthSquared, ref localDirection, ref sweep, ref localTransformB, out hit))
            {
                //Compute the actual hit location on the minkowski surface.
                FPVector3 minkowskiRayHit = -hit.T * localDirection;
                //TODO: This uses MPR to identify a witness point on shape A.
                //It's a very roundabout way to do it.  There should be a much simpler/faster way to compute the witness point directly, or with a little sampling. 
                GetLocalPosition(shapeA, shapeB, ref localTransformB, ref minkowskiRayHit, out hit.Location);
                //The hit location is still in local space, so transform it into world space using A's transform.
                RigidTransform.Transform(ref hit.Location, ref transformA, out hit.Location);
                FPQuaternion.Transform(ref hit.Normal, ref transformA.Orientation, out hit.Normal);
                //Push the world space hit location relative to object A along A's sweep direction.
                FPVector3 temp;
                FPVector3.Multiply(ref sweepA, hit.T, out temp);
                FPVector3.Add(ref temp, ref hit.Location, out hit.Location);
                return true;
            }
            return false;

        }

        private static bool LocalSweepCast(ConvexShape shape, ConvexShape shapeB, FP sweepLength, FP rayLengthSquared, ref FPVector3 localDirection, ref FPVector3 sweep, ref RigidTransform localTransformB, out FPRayHit hit)
        {
            //By now, the ray is known to be within the swept shape and facing the right direction for a normal raycast.

            //First guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = localDirection;
            FPVector3 v1, v1A;
            GetSweptExtremePoint(shape, shapeB, ref localTransformB, ref sweep, ref n, out v1A, out v1);
            //v1 could be zero in some degenerate cases.
            //if (v1.LengthSquared() < Toolbox.Epsilon)
            //{
            //    hit.T = 0;
            //    Vector3.Normalize(ref n, out hit.Normal);
            //    Vector3.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
            //    //hit.Location = hit.T * localDirection;
            //    Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
            //    Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);
            //    hit.Location += sweepA * hit.T;
            //    return true;
            //}

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2, v2A;
            FPVector3.Cross(ref localDirection, ref v1, out n);
            hit.Location = new FPVector3();
            if (n.LengthSquared() < Toolbox.Epsilon * F64.C0p01)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!

                if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
                    FPVector3.Divide(ref localDirection, FP.Sqrt(rayLengthSquared), out hit.Normal);
                else
                    hit.Normal = new FPVector3();

                FP rate;
                FPVector3.Dot(ref  hit.Normal, ref localDirection, out rate);
                FP distance;
                FPVector3.Dot(ref  hit.Normal, ref v1, out distance);
                if (rate > F64.C0)
                    hit.T = sweepLength - distance / rate;
                else
                    hit.T = sweepLength;

                if (hit.T < F64.C0)
                    hit.T = F64.C0;

                //Vector3.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
                ////hit.Location = hit.T * localDirection;
                //Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
                //Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);
                //hit.Location += sweepA * hit.T;
                return hit.T <= F64.C1;


            }
            GetSweptExtremePoint(shape, shapeB, ref localTransformB, ref sweep, ref n, out v2A, out v2);




            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Cross(ref v1, ref v2, out n);

            //It's possible that v1 and v2 were constructed in such a way that 'n' is not properly calibrated
            //relative to the direction vector.
            FP dot;
            FPVector3.Dot(ref n, ref localDirection, out dot);
            if (dot > F64.C0)
            {
                //It's not properly calibrated.  Flip the winding (and the previously calculated normal).
                FPVector3.Negate(ref n, out n);
                temp1 = v1;
                v1 = v2;
                v2 = temp1;
                temp1 = v1A;
                v1A = v2A;
                v2A = temp1;
            }

            FPVector3 v3, v3A;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                GetSweptExtremePoint(shape, shapeB, ref localTransformB, ref sweep, ref n, out v3A, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                {
                    //Can't enclose the origin! That's a bit odd.  Something is wrong; the preparation for this raycast
                    //guarantees that the origin is enclosed.  Could be a numerical problem.
                    hit.T = FP.MaxValue;
                    hit.Normal = new FPVector3();
                    hit.Location = new FPVector3();
                    return false;
                }
                count++;

                //By now, the simplex is a tetrahedron, but it is not known whether or not the ray actually passes through the portal
                //defined by v1, v2, v3.

                // If the direction is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FPVector3.Dot(ref temp1, ref localDirection, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    v2A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v1, ref v3, out n);
                    continue;
                }

                // If the direction is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref localDirection, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    v1A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v2, ref v3, out n);
                    continue;
                }
                break;
            }


            // Refine the portal.
            count = 0;
            while (true)
            {
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v1, ref v2, out temp1);
                FPVector3.Subtract(ref v3, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);


                //Keep working towards the surface.  Find the next extreme point.
                FPVector3 v4, v4A;
                GetSweptExtremePoint(shape, shapeB, ref localTransformB, ref sweep, ref n, out v4A, out v4);


                //If the plane which generated the normal is very close to the extreme point, then we're at the surface.
                FPVector3.Dot(ref n, ref v1, out dot);
                FP supportDot;
                FPVector3.Dot(ref v4, ref n, out supportDot);

                if (supportDot - dot < rayCastSurfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //The portal is now on the surface.  The algorithm can now compute the TOI and exit.
                    FP lengthSquared = n.LengthSquared();
                    if (lengthSquared > Toolbox.Epsilon * F64.C1em5)
                    {
                        FPVector3.Divide(ref n, FP.Sqrt(lengthSquared), out hit.Normal);

                        //The plane is very close to the surface, and the ray is known to pass through it.
                        //dot is the rate.
                        FPVector3.Dot(ref  hit.Normal, ref localDirection, out dot);
                        //supportDot is the distance to the plane.
                        FPVector3.Dot(ref  hit.Normal, ref v1, out supportDot);


                        hit.T = sweepLength - supportDot / dot;
                    }
                    else
                    {
                        FPVector3.Normalize(ref localDirection, out hit.Normal);
                        hit.T = sweepLength;
                    }
                    //Sometimes, when the objects are intersecting, the T parameter can be negative.
                    //In this case, just go with t = 0.
                    if (hit.T < F64.C0)
                        hit.T = F64.C0;


                    //Vector3.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);

                    //Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
                    //Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);
                    //hit.Location += sweepA * (hit.T);

                    //Compute the barycentric coordinates of the ray hit location.
                    //Vector3 mdHitLocation = t * localDirection;
                    //Fix64 v1Weight, v2Weight, v3Weight;
                    //Toolbox.GetBarycentricCoordinates(ref mdHitLocation, ref v1, ref v2, ref v3, out v1Weight, out v2Weight, out v3Weight);
                    //hit.Location = v1Weight * v1A + v2Weight * v2A + v3Weight * v3A;
                    //hit.Location += sweepA * hit.T;

                    //Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
                    //Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);

                    return hit.T <= F64.C1;
                }

                //Still haven't exited, so refine the portal.
                //Test direction against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)


                //This may look a little weird at first.
                //'inside' here means 'on the positive side of the plane.'
                //There are three total planes being tested, one for each of v1, v2, and v3.
                //The planes are created from consistently wound vertices, so it's possible to determine
                //where the ray passes through the portal based upon its relationship to two of the three planes.
                //The third vertex which is found to be opposite the face which contains the ray is replaced with the extreme point.

                //This v4 x direction is just a minor reordering of a scalar triple product: (v1 x v4) * direction.
                //It eliminates the need for extra cross products for the inner if.
                FPVector3.Cross(ref v4, ref localDirection, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                        v1A = v4A;
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                        v3A = v4A;
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                        v2A = v4A;
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                        v1A = v4A;
                    }
                }

                count++;

            }
        }


        /// <summary>
        /// Computes the position of the minkowski point in the local space of A.
        /// This assumes that the minkowski point is contained in A-B.
        /// </summary>
        /// <param name="shapeA">First shape to test.</param>
        /// <param name="shapeB">Second shape to test.</param>
        /// <param name="localTransformB">Transform of shape B in the local space of A.</param>
        /// <param name="minkowskiPosition">Position in minkowski space to pull into the local space of A.</param>
        /// <param name="position">Position of the minkowski space point in the local space of A.</param>
        internal static void GetLocalPosition(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, ref FPVector3 minkowskiPosition, out FPVector3 position)
        {
            //Compute the ray.  This points from a point known to be inside the minkowski sum to the test minkowski position;
            //The centers of the shapes are used to create the interior point.
            FPVector3 rayDirection;
            FPVector3.Add(ref minkowskiPosition, ref localTransformB.Position, out rayDirection);

            //It's possible that the point is extremely close to the A-B center.  In this case, early out.
            if (rayDirection.LengthSquared() < Toolbox.Epsilon)
            {
                //0,0,0 is the contributing position from object A if it overlaps with the A-B minkowski center.
                //A-B center is fromed from the center position of A minus the center position of B.  We're in A's local space.
                position = new FPVector3();
                //DEBUGlastPosition = position;
                return;
            }

            FPVector3 v0;
            FPVector3.Negate(ref localTransformB.Position, out v0); //Since we're in A's local space, A-B is just -B.



            //Now that the ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = rayDirection;
            FPVector3 v1;
            FPVector3 v1A, v1B; //extreme point contributions from each shape.  Used later to compute contact position; could be used to cache simplex too.
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v1A, out v1B, out v1);

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3 v2A, v2B;
            FPVector3.Cross(ref v1, ref v0, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!
                //If the origin is further along this direction than the extreme point, then there is no intersection.
                //If the origin is within this extreme point, then there is an intersection.
                //For this test, we already guarantee that the point is extremely close to the A-B shape or inside of it, so don't bother
                //trying to return false.
                FP dot = FPVector3.Dot(v1 - minkowskiPosition, rayDirection);
                //Vector3.Dot(ref v1, ref rayDirection, out dot);
                //if (dot < 0) //if we were trying to return false here (in a IsPointContained style test), then the '0' should actually be a dot between the minkowskiPoint and rayDirection (simplified by a subtraction and then a dot).
                //{
                //    //Origin is outside.
                //    position = new Vector3();
                //    return false;
                //}
                //Origin is inside.
                //Compute barycentric coordinates along simplex (segment).
                FP dotv0 = FPVector3.Dot(v0 - minkowskiPosition, rayDirection);
                //Dot > 0, so dotv0 starts out negative.
                //Vector3.Dot(ref v0, ref rayDirection, out dotv0);
                FP barycentricCoordinate = -dotv0 / (dot - dotv0);
                //Vector3.Subtract(ref v1A, ref v0A, out offset); //'v0a' is just the zero vector, so there's no need to calculate the offset.
                FPVector3.Multiply(ref v1A, barycentricCoordinate, out position);
                FPVector3 offset;
                FPVector3.Subtract(ref v1B, ref localTransformB.Position, out offset);
                return;
            }
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v2A, out v2B, out v2);

            FPVector3 temp;
            FPVector3 v0v1, v0v2;
            //Set n for the first iteration.
            FPVector3.Subtract(ref v1, ref v0, out v0v1);
            FPVector3.Subtract(ref v2, ref v0, out v0v2);
            FPVector3.Cross(ref v0v1, ref v0v2, out n);

            FPVector3 pointToV0;
            FPVector3.Subtract(ref v0, ref minkowskiPosition, out pointToV0);
            FPVector3 v3A, v3B, v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v3A, out v3B, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                    break;
                count++;
                //By now, the simplex is a tetrahedron, but it is not known whether or not the origin ray found earlier actually passes through the portal
                //defined by v1, v2, v3.

                // If the origin is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3 v0v3;
                FPVector3.Subtract(ref v1, ref v0, out v0v1);
                FPVector3.Subtract(ref v3, ref v0, out v0v3);
                FPVector3.Cross(ref v0v1, ref v0v3, out temp);
                FP dot;
                FPVector3.Dot(ref temp, ref pointToV0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    v2A = v3A;
                    v2B = v3B;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v0v1, ref v0v3, out n);
                    continue;
                }

                // If the origin is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Subtract(ref v2, ref v0, out v0v2);
                FPVector3.Cross(ref v0v3, ref v0v2, out temp);
                FPVector3.Dot(ref temp, ref pointToV0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    v1A = v3A;
                    v1B = v3B;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v0v2, ref v0v3, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref rayDirection))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            while (true)
            {
                //Test the origin against the plane defined by v1, v2, v3.  If it's inside, we're done.
                //Compute the outward facing normal.
                FPVector3 v2v3, v2v1;
                FPVector3.Subtract(ref v3, ref v2, out v2v3);
                FPVector3.Subtract(ref v1, ref v2, out v2v1);
                FPVector3.Cross(ref v2v3, ref v2v1, out n);
                FP dot;
                FPVector3 pointToV1;
                FPVector3.Subtract(ref v1, ref minkowskiPosition, out pointToV1);
                FPVector3.Dot(ref pointToV1, ref n, out dot);
                //Because this method is intended for use with surface collisions, rely on the surface push case.
                //This will not significantly harm performance, but will simplify the termination condition.
                //if (dot >= 0)
                //{
                //    Vector3 temp3;
                //    //Compute the barycentric coordinates of the origin.
                //    //This is done by computing the scaled volume (parallelepiped) of the tetrahedra 
                //    //formed by each triangle of the v0v1v2v3 tetrahedron and the origin.

                //    //TODO: consider a different approach using T parameter or something.
                //    Vector3.Subtract(ref v1, ref v0, out temp1);
                //    Vector3.Subtract(ref v2, ref v0, out temp2);
                //    Vector3.Subtract(ref v3, ref v0, out temp3);

                //    Vector3 cross;
                //    Vector3.Cross(ref temp1, ref temp2, out cross);
                //    Fix64 v0v1v2v3volume;
                //    Vector3.Dot(ref cross, ref temp3, out v0v1v2v3volume);

                //    Vector3.Cross(ref v1, ref v2, out cross);
                //    Fix64 ov1v2v3volume;
                //    Vector3.Dot(ref cross, ref v3, out ov1v2v3volume);

                //    Vector3.Cross(ref rayDirection, ref temp2, out cross);
                //    Fix64 v0ov2v3volume;
                //    Vector3.Dot(ref cross, ref temp3, out v0ov2v3volume);

                //    Vector3.Cross(ref temp1, ref rayDirection, out cross);
                //    Fix64 v0v1ov3volume;
                //    Vector3.Dot(ref cross, ref temp3, out v0v1ov3volume);

                //    if (v0v1v2v3volume > Toolbox.Epsilon * .01f)
                //    {
                //        Fix64 inverseTotalVolume = 1 / v0v1v2v3volume;
                //        Fix64 v0Weight = ov1v2v3volume * inverseTotalVolume;
                //        Fix64 v1Weight = v0ov2v3volume * inverseTotalVolume;
                //        Fix64 v2Weight = v0v1ov3volume * inverseTotalVolume;
                //        Fix64 v3Weight = 1 - v0Weight - v1Weight - v2Weight;
                //        position = v1Weight * v1A + v2Weight * v2A + v3Weight * v3A;
                //    }
                //    else
                //    {
                //        position = new Vector3();
                //    }
                //    //DEBUGlastPosition = position;
                //    return;
                //}

                //We haven't yet found the origin.  Find the support point in the portal's outward facing direction.
                FPVector3 v4, v4A, v4B;
                MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref n, ref localTransformB, out v4A, out v4B, out v4);
                //If the origin is further along the direction than the extreme point, it's not inside the shape.
                FP dot2;
                FPVector3 pointToV4;
                FPVector3.Subtract(ref v4, ref minkowskiPosition, out pointToV4);
                FPVector3.Dot(ref pointToV4, ref n, out dot2);
                //if (dot2 < 0) //We're not concerned with this test!  We already have guarantees that the shape contains or very nearly contains the minkowski point.
                //{
                //    //The origin is outside!
                //    position = new Vector3();
                //    return false;
                //}

                //If the plane which generated the normal is very close to the extreme point, then we're at the surface
                //and we have not found the origin; it's either just BARELY inside, or it is outside.  Assume it's outside.
                if (dot2 - dot < rayCastSurfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //We found the surface.  Technically, we did not find the minkowski point yet, but it must be really close based on the guarantees
                    //required by this method.
                    //The ray intersection with the plane defined by our final portal should be extremely close to the actual minkowski point.
                    //In fact, it is probably close enough such that the barycentric coordinates can be computed using the minkowski point directly!
                    FP weight1, weight2, weight3;
                    Toolbox.GetBarycentricCoordinates(ref minkowskiPosition, ref v1, ref v2, ref v3, out weight1, out weight2, out weight3);
                    FPVector3.Multiply(ref v1A, weight1, out position);
                    FPVector3.Multiply(ref v2A, weight2, out v2A);
                    FPVector3.Multiply(ref v3A, weight3, out v3A);
                    FPVector3.Add(ref v2A, ref position, out position);
                    FPVector3.Add(ref v3A, ref position, out position);

                    return;
                }
                count++;

                //Still haven't exited, so refine the portal.
                //Test minkowskiPoint against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)
                FPVector3.Cross(ref pointToV4, ref pointToV0, out temp);
                FPVector3.Dot(ref pointToV1, ref temp, out dot);
                if (dot >= F64.C0) //Dot v0 x v4 against v1, and dot it against 
                {
                    FPVector3 pointToV2;
                    FPVector3.Subtract(ref v2, ref minkowskiPosition, out pointToV2);
                    FPVector3.Dot(ref pointToV2, ref temp, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                        v1A = v4A;
                        v1B = v4B;
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                        v3A = v4A;
                        v3B = v4B;
                    }
                }
                else
                {
                    FPVector3 pointToV3;
                    FPVector3.Subtract(ref v3, ref minkowskiPosition, out pointToV3);
                    FPVector3.Dot(ref pointToV3, ref temp, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                        v2A = v4A;
                        v2B = v4B;
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                        v1A = v4A;
                        v1B = v4B;
                    }
                }

                //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref rayDirection))
                //    Debug.WriteLine("Break.");

            }


        }

        /// <summary>
        /// Determines if two shapes are intersecting.
        /// </summary>
        /// <param name="shapeA">First shape in the pair.</param>
        /// <param name="shapeB">Second shape in the pair.</param>
        /// <param name="sweep">Sweep direction and magnitude.</param>
        /// <param name="localTransformB">Transformation of shape B in the local space of A.</param>
        /// <param name="position">Position of the minkowski difference origin in the local space of A, if the swept volumes intersect.</param>
        /// <returns>Whether the swept shapes intersect.</returns>
        public static bool AreSweptShapesIntersecting(ConvexShape shapeA, ConvexShape shapeB, ref FPVector3 sweep, ref RigidTransform localTransformB, out FPVector3 position)
        {
            //It's possible that the two objects' centers are overlapping, or very very close to it.  In this case, 
            //they are obviously colliding and we can immediately exit.
            if (localTransformB.Position.LengthSquared() < Toolbox.Epsilon)
            {
                position = new FPVector3();
                return true;
            }

            FPVector3 v0;
            FPVector3.Negate(ref localTransformB.Position, out v0); //Since we're in A's local space, A-B is just -B.



            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = localTransformB.Position;
            FPVector3 v1;
            FPVector3 v1A; //extreme point contributions from each shape.  Used later to compute contact position; could be used to cache simplex too.
            //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v1A, out v1B, out v1);
            GetSweptExtremePoint(shapeA, shapeB, ref localTransformB, ref sweep, ref n, out v1A, out v1);

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3 v2A;
            FPVector3.Cross(ref v1, ref v0, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!
                //If the origin is further along this direction than the extreme point, then there is no intersection.
                //If the origin is within this extreme point, then there is an intersection.
                FP dot;
                FPVector3.Dot(ref v1, ref localTransformB.Position, out dot);
                if (dot < F64.C0)
                {
                    //Origin is outside.
                    position = new FPVector3();
                    return false;
                }
                //Origin is inside.
                //Compute barycentric coordinates along simplex (segment).
                FP dotv0;
                //Dot > 0, so dotv0 starts out negative.
                FPVector3.Dot(ref v0, ref localTransformB.Position, out dotv0);
                FP barycentricCoordinate = -dotv0 / (dot - dotv0);
                //Vector3.Subtract(ref v1A, ref v0A, out offset); //'v0a' is just the zero vector, so there's no need to calculate the offset.
                FPVector3.Multiply(ref v1A, barycentricCoordinate, out position);
                return true;
            }
            //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v2A, out v2B, out v2);
            GetSweptExtremePoint(shapeA, shapeB, ref localTransformB, ref sweep, ref n, out v2A, out v2);

            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Subtract(ref v1, ref v0, out temp1);
            FPVector3.Subtract(ref v2, ref v0, out temp2);
            FPVector3.Cross(ref temp1, ref temp2, out n);


            FPVector3 v3A, v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v3A, out v3B, out v3);
                GetSweptExtremePoint(shapeA, shapeB, ref localTransformB, ref sweep, ref n, out v3A, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                    break;
                count++;
                //By now, the simplex is a tetrahedron, but it is not known whether or not the origin ray found earlier actually passes through the portal
                //defined by v1, v2, v3.

                // If the origin is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FP dot;
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    v2A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }

                // If the origin is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    v1A = v3A;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v2, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            while (true)
            {
                //Test the origin against the plane defined by v1, v2, v3.  If it's inside, we're done.
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v3, ref v2, out temp1);
                FPVector3.Subtract(ref v1, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);
                FP dot;
                FPVector3.Dot(ref n, ref v1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3 temp3;
                    //Compute the barycentric coordinates of the origin.
                    //This is done by computing the scaled volume (parallelepiped) of the tetrahedra 
                    //formed by each triangle of the v0v1v2v3 tetrahedron and the origin.

                    //TODO: consider a different approach using T parameter or something.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v2, ref v0, out temp2);
                    FPVector3.Subtract(ref v3, ref v0, out temp3);

                    FPVector3 cross;
                    FPVector3.Cross(ref temp1, ref temp2, out cross);
                    FP v0v1v2v3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0v1v2v3volume);

                    FPVector3.Cross(ref v1, ref v2, out cross);
                    FP ov1v2v3volume;
                    FPVector3.Dot(ref cross, ref v3, out ov1v2v3volume);

                    FPVector3.Cross(ref localTransformB.Position, ref temp2, out cross);
                    FP v0ov2v3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0ov2v3volume);

                    FPVector3.Cross(ref temp1, ref localTransformB.Position, out cross);
                    FP v0v1ov3volume;
                    FPVector3.Dot(ref cross, ref temp3, out v0v1ov3volume);


                    FP inverseTotalVolume = F64.C1 / v0v1v2v3volume;
                    FP v0Weight = ov1v2v3volume * inverseTotalVolume;
                    FP v1Weight = v0ov2v3volume * inverseTotalVolume;
                    FP v2Weight = v0v1ov3volume * inverseTotalVolume;
                    FP v3Weight = F64.C1 - v0Weight - v1Weight - v2Weight;
                    position = v1Weight * v1A + v2Weight * v2A + v3Weight * v3A;
                    //DEBUGlastPosition = position;
                    return true;
                }

                //We haven't yet found the origin.  Find the support point in the portal's outward facing direction.
                FPVector3 v4, v4A;
                //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v4A, out v4B, out v4); 
                GetSweptExtremePoint(shapeA, shapeB, ref localTransformB, ref sweep, ref n, out v4A, out v4);

                //If the origin is further along the direction than the extreme point, it's not inside the shape.
                FP dot2;
                FPVector3.Dot(ref v4, ref n, out dot2);
                if (dot2 < F64.C0)
                {
                    //The origin is outside!
                    position = new FPVector3();
                    return false;
                }

                //If the plane which generated the normal is very close to the extreme point, then we're at the surface
                //and we have not found the origin; it's either just BARELY inside, or it is outside.  Assume it's outside.
                if (dot2 - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    position = new FPVector3();
                    //DEBUGlastPosition = position;
                    return false;
                }
                count++;

                //Still haven't exited, so refine the portal.
                //Test origin against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)
                FPVector3.Cross(ref v4, ref v0, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                        v1A = v4A;
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                        v3A = v4A;
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                        v2A = v4A;
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                        v1A = v4A;
                    }
                }

                //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
                //    Debug.WriteLine("Break.");

            }


        }


        static void GetSweptExtremePoint(ConvexShape shapeA, ConvexShape shapeB, ref RigidTransform localTransformB, ref FPVector3 sweep, ref FPVector3 extremePointDirection, out FPVector3 extremePointA, out FPVector3 extremePoint)
        {
            FPVector3 b;
            MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shapeA, shapeB, ref extremePointDirection, ref localTransformB, out extremePointA, out b, out extremePoint);
            FP dot;
            FPVector3.Dot(ref extremePointDirection, ref sweep, out dot);
            if (dot > F64.C0)
            {
                FPVector3.Add(ref extremePoint, ref sweep, out extremePoint);
            }
        }

        //RAY CAST
        /// <summary>
        /// Casts a ray against a shape.
        /// </summary>
        /// <param name="ray">Ray to test against the shape.</param>
        /// <param name="maximumLength">Maximum length of the ray.</param>
        /// <param name="shape">Shape to test with a ray.</param>
        /// <param name="transform">Transform to apply to the shape.</param>
        /// <param name="hit">Hit data of the ray on the shape, if any.</param>
        /// <returns>Whether or not the swept shapes hit each other.</returns>
        public static bool RayCast(FPRay ray, FP maximumLength, ConvexShape shape, ref RigidTransform transform, out FPRayHit hit)
        {
            //Compute the local origin and direction of the ray.
            FPRay localRay;
            FPQuaternion conjugate;
            FPVector3.Subtract(ref ray.Position, ref transform.Position, out localRay.Position);
            FPQuaternion.Conjugate(ref transform.Orientation, out conjugate);
            FPQuaternion.Transform(ref localRay.Position, ref conjugate, out localRay.Position);
            FPQuaternion.Transform(ref ray.Direction, ref conjugate, out localRay.Direction);

            //Note that we will be casting the ray *backwards* against the sweep-expanded surface of the shape.
            FPVector3.Negate(ref localRay.Direction, out localRay.Direction);

            //A ray cast is a special case of two-body sweeping.  This is implemented separately from the two-body sweep above almost entirely for a tiny performance boost.
            //It also allows for certain tricks to help with numerical stability.

            //Sweeping two objects against each other is very similar to the local surface cast.
            //The ray starts at the origin and goes in the sweep direction.
            //However, unlike the local surface cast, the origin may start outside of the minkowski difference.
            //Additionally, the method can early out if the length traversed by the ray is found to be longer than the maximum length, or if the origin is found to be outside the minkowski difference.

            //The support points of the minkowski difference are also modified.  By default, the minkowski difference should very rarely contain the origin.
            //Sweep tests aren't very useful if the objects are intersecting!
            //However, in order for the local surface cast to actually find a proper result (assuming there is a hit at all), the origin must be inside the minkowski difference.
            //So, expand the minkowski difference using the sweep direction with magnitude sufficient to fully include the plane defined by the origin and the sweep direction.
            //If there's going to be a hit, then the origin will be within this expanded shape.



            //If the sweep direction is found to be negative, the ray can be thought of as pointing away from the shape.
            //However, the minkowski difference may contain the shape.  In this case, the time of impact is zero.

            //If the swept (with sweep = 0 in case of incorrect direction) minkowski difference does not contain the shape, then the raycast cannot begin and we also know that the shapes will not intersect.

            //If the sweep amount is nonnegative and the minkowski difference contains the shape, then the normal raycasting process can continue.
            //Perform the usual local raycast, but use the swept minkowski difference.
            //Once the surface is found, the final t parameter of the ray is equal to the sweep distance minus the local raycast computed t parameter.


            //Given that this is a ray cast, we can make some modifications.  Rays frequently traverse large distances, but stretching the swept volume
            //a huge amount to match could manifest numerical issues.  Instead, pull the ray up close to the object.
            FPRayHit sphereHit;

            if (Toolbox.RayCastSphere(ref ray, ref transform.Position, shape.MaximumRadius, maximumLength, out sphereHit))
            {
                //We can scoot ourselves almost all the way up to the intersection with the outer sphere.
                //Stop just short to prevent a possible erroneous 'just-barely-contained' result.
                sphereHit.T = MathHelper.Max(sphereHit.T - F64.C0p1, F64.C0);
                FPVector3 offset;
                FPVector3.Multiply(ref localRay.Direction, -sphereHit.T, out offset);
                FPVector3.Add(ref localRay.Position, ref offset, out localRay.Position);
            }
            else
            {
                //If the ray cast doesn't hit the bounding sphere, it's impossible for the ray to hit the shape itself.
                hit.T = FP.MaxValue;
                hit.Normal = new FPVector3();
                hit.Location = new FPVector3();
                return false;
            }



            //First: Compute the sweep amount along the sweep direction.
            //This sweep amount needs to expand the minkowski difference to fully intersect the plane defined by the sweep direction and origin.

            FP rayLengthSquared = localRay.Direction.LengthSquared();
            FP sweepLength;
            if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
            {
                FPVector3.Dot(ref localRay.Position, ref localRay.Direction, out sweepLength);
                //Ray length isn't necessarily normalized...
                sweepLength /= rayLengthSquared;
                //Scale the sweep length by the margins.  Divide by the length to pull the margin into terms of the length of the ray.
                sweepLength += shape.MaximumRadius / FP.Sqrt(rayLengthSquared);
            }
            else
            {
                rayLengthSquared = F64.C0;
                sweepLength = F64.C0;
            }
            //If the sweep direction is found to be negative, the ray can be thought of as pointing away from the shape.
            //Do not sweep backward.
            bool negativeLength = sweepLength < F64.C0;
            if (negativeLength)
                sweepLength = F64.C0;



            FPVector3 sweep;
            FPVector3.Multiply(ref localRay.Direction, sweepLength, out sweep);
            //Check to see if the origin is contained within the swept shape.
            if (!SweptShapeContainsPoint(shape, ref sweep, ref localRay.Position))
            {
                //The origin is not contained within the sweep volume.  The raycast definitely misses.
                hit.T = FP.MaxValue;
                hit.Normal = new FPVector3();
                hit.Location = new FPVector3();
                return false;
            }
            if (negativeLength)
            {
                //The origin is contained, but we shouldn't continue.
                //The ray is facing backwards.  The time of impact would be 0 (because we already verified that we are contained in the shape volume).
                hit.T = F64.C0;
                FPVector3.Normalize(ref ray.Direction, out hit.Normal);
                hit.Location = ray.Position;
                return true;
            }

            //OKAY! We've finally finished all the pre-testing.  Cast the ray!
            if (LocalSweepCast(shape, sweepLength, rayLengthSquared, ref localRay.Direction, ref sweep, ref localRay.Position, out hit))
            {
                hit.T += sphereHit.T;
                if (hit.T <= maximumLength)
                {
                    //Get the world space hit location.
                    FPVector3.Multiply(ref ray.Direction, hit.T, out hit.Location);
                    FPVector3.Add(ref hit.Location, ref ray.Position, out hit.Location);
                    //Transform the normal.
                    FPQuaternion.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
                    return true;
                }
            }
            return false;

        }

        /// <summary>
        /// Determines if a point is contained within the swept volume of a shape.
        /// </summary>
        /// <param name="shape">First shape in the pair.</param>
        /// <param name="sweep">Sweep direction and magnitude.</param>
        /// <param name="localPoint">Point to check for containment in the shape in the local space of the shape.</param>
        /// <returns>Whether the swept shape contains the point.</returns>
        public static bool SweptShapeContainsPoint(ConvexShape shape, ref FPVector3 sweep, ref FPVector3 localPoint)
        {
            //It's possible that the two objects' centers are overlapping, or very very close to it.  In this case, 
            //they are obviously colliding and we can immediately exit.
            if (localPoint.LengthSquared() < Toolbox.Epsilon)
            {
                return true;
            }

            FPVector3 v0;
            FPVector3.Negate(ref localPoint, out v0); //Since we're in A's local space, A-B is just -B.



            //Now that the origin ray is known, create a portal through which the ray passes.
            //To do this, first guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = localPoint;
            FPVector3 v1;
            //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v1A, out v1B, out v1);
            GetSweptExtremePoint(shape, ref localPoint, ref sweep, ref n, out v1);

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3.Cross(ref v1, ref v0, out n);
            if (n.LengthSquared() < Toolbox.Epsilon)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!
                //If the origin is further along this direction than the extreme point, then there is no intersection.
                //If the origin is within this extreme point, then there is an intersection.
                FP dot;
                FPVector3.Dot(ref v1, ref localPoint, out dot);
                if (dot < F64.C0)
                {
                    //Origin is outside.
                    return false;
                }
                return true;
            }
            //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v2A, out v2B, out v2);
            GetSweptExtremePoint(shape, ref localPoint, ref sweep, ref n, out v2);

            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Subtract(ref v1, ref v0, out temp1);
            FPVector3.Subtract(ref v2, ref v0, out temp2);
            FPVector3.Cross(ref temp1, ref temp2, out n);


            FPVector3 v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v3A, out v3B, out v3);
                GetSweptExtremePoint(shape, ref localPoint, ref sweep, ref n, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                    break;
                count++;
                //By now, the simplex is a tetrahedron, but it is not known whether or not the origin ray found earlier actually passes through the portal
                //defined by v1, v2, v3.

                // If the origin is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FP dot;
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v1, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }

                // If the origin is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref v0, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Subtract(ref v2, ref v0, out temp1);
                    FPVector3.Subtract(ref v3, ref v0, out temp2);
                    FPVector3.Cross(ref temp1, ref temp2, out n);
                    continue;
                }
                break;
            }

            //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
            //    Debug.WriteLine("Break.");


            // Refine the portal.
            while (true)
            {
                //Test the origin against the plane defined by v1, v2, v3.  If it's inside, we're done.
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v3, ref v2, out temp1);
                FPVector3.Subtract(ref v1, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);
                FP dot;
                FPVector3.Dot(ref n, ref v1, out dot);
                if (dot >= F64.C0)
                {
                    return true;
                }

                //We haven't yet found the origin.  Find the support point in the portal's outward facing direction.
                FPVector3 v4;
                //MinkowskiToolbox.GetLocalMinkowskiExtremePoint(shape, shapeB, ref n, ref localPoint, out v4A, out v4B, out v4); 
                GetSweptExtremePoint(shape, ref localPoint, ref sweep, ref n, out v4);

                //If the origin is further along the direction than the extreme point, it's not inside the shape.
                FP dot2;
                FPVector3.Dot(ref v4, ref n, out dot2);
                if (dot2 < F64.C0)
                {
                    //The origin is outside!
                    return false;
                }

                //If the plane which generated the normal is very close to the extreme point, then we're at the surface
                //and we have not found the origin; it's either just BARELY inside, or it is outside.  Assume it's outside.
                if (dot2 - dot < surfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //DEBUGlastPosition = position;
                    return false;
                }
                count++;

                //Still haven't exited, so refine the portal.
                //Test origin against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)
                FPVector3.Cross(ref v4, ref v0, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                    }
                }

                //if (!VerifySimplex(ref v0, ref v1, ref v2, ref v3, ref localPoint.Position))
                //    Debug.WriteLine("Break.");

            }


        }


        private static bool LocalSweepCast(ConvexShape shape, FP sweepLength, FP rayLengthSquared, ref FPVector3 localDirection, ref FPVector3 sweep, ref FPVector3 rayOrigin, out FPRayHit hit)
        {
            //By now, the ray is known to be within the swept shape and facing the right direction for a normal raycast.

            //First guess a portal.
            //This implementation is similar to that of the original XenoCollide.
            //'n' will be the direction used to find supports throughout the algorithm.
            FPVector3 n = localDirection;
            FPVector3 v1;
            GetSweptExtremePoint(shape, ref rayOrigin, ref sweep, ref n, out v1);
            //v1 could be zero in some degenerate cases.
            //if (v1.LengthSquared() < Toolbox.Epsilon)
            //{
            //    hit.T = 0;
            //    Vector3.Normalize(ref n, out hit.Normal);
            //    Vector3.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
            //    //hit.Location = hit.T * localDirection;
            //    Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
            //    Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);
            //    hit.Location += sweepA * hit.T;
            //    return true;
            //}

            //Find another extreme point in a direction perpendicular to the previous.
            FPVector3 v2;
            FPVector3.Cross(ref localDirection, ref v1, out n);
            hit.Location = new FPVector3();
            if (n.LengthSquared() < Toolbox.Epsilon * F64.C0p01)
            {
                //v1 and v0 could be parallel.
                //This isn't a bad thing- it means the direction is exactly aligned with the extreme point offset.
                //In other words, if the raycast is followed out to the surface, it will arrive at the extreme point!

                if (rayLengthSquared > Toolbox.Epsilon * F64.C0p01)
                    FPVector3.Divide(ref localDirection, FP.Sqrt(rayLengthSquared), out hit.Normal);
                else
                    hit.Normal = new FPVector3();

                FP rate;
                FPVector3.Dot(ref  hit.Normal, ref localDirection, out rate);
                FP distance;
                FPVector3.Dot(ref  hit.Normal, ref v1, out distance);
                if (rate > F64.C0)
                    hit.T = sweepLength - distance / rate;
                else
                    hit.T = sweepLength;

                if (hit.T < F64.C0)
                    hit.T = F64.C0;

                //Vector3.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
                ////hit.Location = hit.T * localDirection;
                //Vector3.Transform(ref hit.Location, ref transform.Orientation, out hit.Location);
                //Vector3.Add(ref hit.Location, ref transform.Position, out hit.Location);
                //hit.Location += sweepA * hit.T;
                return hit.T <= F64.C1;


            }
            GetSweptExtremePoint(shape, ref rayOrigin, ref sweep, ref n, out v2);




            FPVector3 temp1, temp2;
            //Set n for the first iteration.
            FPVector3.Cross(ref v1, ref v2, out n);

            //It's possible that v1 and v2 were constructed in such a way that 'n' is not properly calibrated
            //relative to the direction vector.
            FP dot;
            FPVector3.Dot(ref n, ref localDirection, out dot);
            if (dot > F64.C0)
            {
                //It's not properly calibrated.  Flip the winding (and the previously calculated normal).
                FPVector3.Negate(ref n, out n);
                temp1 = v1;
                v1 = v2;
                v2 = temp1;
            }

            FPVector3 v3;
            int count = 0;
            while (true)
            {
                //Find a final extreme point using the normal of the plane defined by v0, v1, v2.
                GetSweptExtremePoint(shape, ref rayOrigin, ref sweep, ref n, out v3);

                if (count > MPRToolbox.OuterIterationLimit)
                {
                    //Can't enclose the origin! That's a bit odd.  Something is wrong; the preparation for this raycast
                    //guarantees that the origin is enclosed.  Could be a numerical problem.
                    hit.T = FP.MaxValue;
                    hit.Normal = new FPVector3();
                    hit.Location = new FPVector3();
                    return false;
                }
                count++;

                //By now, the simplex is a tetrahedron, but it is not known whether or not the ray actually passes through the portal
                //defined by v1, v2, v3.

                // If the direction is outside the plane defined by v1,v0,v3, then the portal is invalid.
                FPVector3.Cross(ref v1, ref v3, out temp1);
                FPVector3.Dot(ref temp1, ref localDirection, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v2) with the new extreme point.
                    v2 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v1, ref v3, out n);
                    continue;
                }

                // If the direction is outside the plane defined by v3,v0,v2, then the portal is invalid.
                FPVector3.Cross(ref v3, ref v2, out temp1);
                FPVector3.Dot(ref temp1, ref localDirection, out dot);
                if (dot < F64.C0)
                {
                    //Replace the point that was on the inside of the plane (v1) with the new extreme point.
                    v1 = v3;
                    // Calculate the normal of the plane that will be used to find a new extreme point.
                    FPVector3.Cross(ref v2, ref v3, out n);
                    continue;
                }
                break;
            }


            // Refine the portal.
            count = 0;
            while (true)
            {
                //Compute the outward facing normal.
                FPVector3.Subtract(ref v1, ref v2, out temp1);
                FPVector3.Subtract(ref v3, ref v2, out temp2);
                FPVector3.Cross(ref temp1, ref temp2, out n);


                //Keep working towards the surface.  Find the next extreme point.
                FPVector3 v4;
                GetSweptExtremePoint(shape, ref rayOrigin, ref sweep, ref n, out v4);


                //If the plane which generated the normal is very close to the extreme point, then we're at the surface.
                FPVector3.Dot(ref n, ref v1, out dot);
                FP supportDot;
                FPVector3.Dot(ref v4, ref n, out supportDot);

                if (supportDot - dot < rayCastSurfaceEpsilon || count > MPRToolbox.InnerIterationLimit) // TODO: Could use a dynamic epsilon for possibly better behavior.
                {
                    //The portal is now on the surface.  The algorithm can now compute the TOI and exit.
                    FP lengthSquared = n.LengthSquared();
                    if (lengthSquared > Toolbox.Epsilon * F64.C1em5)
                    {
                        FPVector3.Divide(ref n, FP.Sqrt(lengthSquared), out hit.Normal);

                        //The plane is very close to the surface, and the ray is known to pass through it.
                        //dot is the rate.
                        FPVector3.Dot(ref  hit.Normal, ref localDirection, out dot);
                        //supportDot is the distance to the plane.
                        FPVector3.Dot(ref  hit.Normal, ref v1, out supportDot);


                        hit.T = sweepLength - supportDot / dot;
                    }
                    else
                    {
                        FPVector3.Normalize(ref localDirection, out hit.Normal);
                        hit.T = sweepLength;
                    }
                    //Sometimes, when the objects are intersecting, the T parameter can be negative.
                    //In this case, just go with t = 0.
                    if (hit.T < F64.C0)
                        hit.T = F64.C0;
                    return true;
                }

                //Still haven't exited, so refine the portal.
                //Test direction against the three planes that separate the new portal candidates: (v1,v4,v0) (v2,v4,v0) (v3,v4,v0)


                //This may look a little weird at first.
                //'inside' here means 'on the positive side of the plane.'
                //There are three total planes being tested, one for each of v1, v2, and v3.
                //The planes are created from consistently wound vertices, so it's possible to determine
                //where the ray passes through the portal based upon its relationship to two of the three planes.
                //The third vertex which is found to be opposite the face which contains the ray is replaced with the extreme point.

                //This v4 x direction is just a minor reordering of a scalar triple product: (v1 x v4) * direction.
                //It eliminates the need for extra cross products for the inner if.
                FPVector3.Cross(ref v4, ref localDirection, out temp1);
                FPVector3.Dot(ref v1, ref temp1, out dot);
                if (dot >= F64.C0)
                {
                    FPVector3.Dot(ref v2, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v1 = v4; // Inside v1 & inside v2 ==> eliminate v1
                    }
                    else
                    {
                        v3 = v4; // Inside v1 & outside v2 ==> eliminate v3
                    }
                }
                else
                {
                    FPVector3.Dot(ref v3, ref temp1, out dot);
                    if (dot >= F64.C0)
                    {
                        v2 = v4; // Outside v1 & inside v3 ==> eliminate v2
                    }
                    else
                    {
                        v1 = v4; // Outside v1 & outside v3 ==> eliminate v1
                    }
                }

                count++;

            }
        }

        static void GetSweptExtremePoint(ConvexShape shape, ref FPVector3 point, ref FPVector3 sweep, ref FPVector3 extremePointDirection, out FPVector3 extremePoint)
        {
            shape.GetExtremePoint(extremePointDirection, ref Toolbox.RigidIdentity, out extremePoint);
            FPVector3.Subtract(ref extremePoint, ref point, out extremePoint);
            FP dot;
            FPVector3.Dot(ref extremePointDirection, ref sweep, out dot);
            if (dot > F64.C0)
            {
                FPVector3.Add(ref extremePoint, ref sweep, out extremePoint);
            }
        }


    }
}
