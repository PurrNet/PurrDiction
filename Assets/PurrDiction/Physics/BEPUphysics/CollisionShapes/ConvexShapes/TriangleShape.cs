using System;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.CollisionShapes.ConvexShapes
{

    ///<summary>
    /// Triangle collision shape.
    ///</summary>
    public class TriangleShape : ConvexShape
    {
        internal FPVector3 vA, vB, vC;

        ///<summary>
        /// Gets or sets the first vertex of the triangle shape.
        ///</summary>
        public FPVector3 VertexA
        {
            get
            {
                return vA;
            }
            set
            {
                vA = value;
                OnShapeChanged();
            }
        }

        ///<summary>
        /// Gets or sets the second vertex of the triangle shape.
        ///</summary>
        public FPVector3 VertexB
        {
            get
            {
                return vB;
            }
            set
            {
                vB = value;
                OnShapeChanged();
            }
        }

        ///<summary>
        /// Gets or sets the third vertex of the triangle shape.
        ///</summary>
        public FPVector3 VertexC
        {
            get
            {
                return vC;
            }
            set
            {
                vC = value;
                OnShapeChanged();
            }
        }

        internal TriangleSidedness sidedness;
        ///<summary>
        /// Gets or sets the sidedness of the triangle.
        ///</summary>
        public TriangleSidedness Sidedness
        {
            get { return sidedness; }
            set
            {
                sidedness = value;
                OnShapeChanged();
            }
        }

        ///<summary>
        /// Constructs a triangle shape without initializing it.
        /// This is useful for systems that re-use a triangle shape repeatedly and do not care about its properties.
        ///</summary>
        public TriangleShape()
        {
            //Triangles are often used in special situations where the vertex locations are changed directly.  This constructor assists with that.
        }

        ///<summary>
        /// Constructs a triangle shape.
        /// The vertices will be recentered.  If the center is needed, use the other constructor.
        ///</summary>
        ///<param name="vA">First vertex in the triangle.</param>
        ///<param name="vB">Second vertex in the triangle.</param>
        ///<param name="vC">Third vertex in the triangle.</param>
        public TriangleShape(FPVector3 vA, FPVector3 vB, FPVector3 vC)
        {
            //Recenter.  Convexes should contain the origin.
            FPVector3 center = (vA + vB + vC) / F64.C3;
            this.vA = vA - center;
            this.vB = vB - center;
            this.vC = vC - center;
            UpdateConvexShapeInfo(ComputeDescription(this.vA, this.vB, this.vC, collisionMargin));
        }

        ///<summary>
        /// Constructs a triangle shape.
        /// The vertices will be recentered.
        ///</summary>
        ///<param name="vA">First vertex in the triangle.</param>
        ///<param name="vB">Second vertex in the triangle.</param>
        ///<param name="vC">Third vertex in the triangle.</param>
        ///<param name="center">Computed center of the triangle.</param>
        public TriangleShape(FPVector3 vA, FPVector3 vB, FPVector3 vC, out FPVector3 center)
        {
            //Recenter.  Convexes should contain the origin.
            center = (vA + vB + vC) / F64.C3;
            this.vA = vA - center;
            this.vB = vB - center;
            this.vC = vC - center;
            UpdateConvexShapeInfo(ComputeDescription(this.vA, this.vB, this.vC, collisionMargin));
        }

        ///<summary>
        /// Constructs a triangle shape from cached data.
        ///</summary>
        ///<param name="vA">First vertex in the triangle.</param>
        ///<param name="vB">Second vertex in the triangle.</param>
        ///<param name="vC">Third vertex in the triangle.</param>
        /// <param name="description">Cached information about the shape. Assumed to be correct; no extra processing or validation is performed.</param>
        public TriangleShape(FPVector3 vA, FPVector3 vB, FPVector3 vC, ConvexShapeDescription description)
        {
            //Recenter.  Convexes should contain the origin.
            var center = (vA + vB + vC) / F64.C3;
            this.vA = vA - center;
            this.vB = vB - center;
            this.vC = vC - center;
            UpdateConvexShapeInfo(description);
        }




        /// <summary>
        /// Computes a convex shape description for a TransformableShape.
        /// </summary>
        ///<param name="vA">First local vertex in the triangle.</param>
        ///<param name="vB">Second local vertex in the triangle.</param>
        ///<param name="vC">Third local vertex in the triangle.</param>
        ///<param name="collisionMargin">Collision margin of the shape.</param>
        /// <returns>Description required to define a convex shape.</returns>
        public static ConvexShapeDescription ComputeDescription(FPVector3 vA, FPVector3 vB, FPVector3 vC, FP collisionMargin)
        {
            ConvexShapeDescription description;
            // A triangle by itself technically has no volume, but shapes try to include the collision margin in the volume when feasible (e.g. BoxShape).
            //Plus, it's convenient to have a nonzero volume for buoyancy.
            var doubleArea = FPVector3.Cross(vB - vA, vC - vA).Length();
            description.EntityShapeVolume.Volume = doubleArea * collisionMargin;

            //Compute the inertia tensor.
            var v = new Matrix3x3(
                vA.x, vA.y, vA.z,
                vB.x, vB.y, vB.z,
                vC.x, vC.y, vC.z);
            var s = new Matrix3x3(
				F64.C2, F64.C1, F64.C1,
				F64.C1, F64.C2, F64.C1,
				F64.C1, F64.C1, F64.C2);

            Matrix3x3.MultiplyTransposed(ref v, ref s, out description.EntityShapeVolume.VolumeDistribution);
            Matrix3x3.Multiply(ref description.EntityShapeVolume.VolumeDistribution, ref v, out description.EntityShapeVolume.VolumeDistribution);
            var scaling = doubleArea / F64.C24;
            Matrix3x3.Multiply(ref description.EntityShapeVolume.VolumeDistribution, -scaling, out description.EntityShapeVolume.VolumeDistribution);

            //The square-of-sum term is ignored since the parameters should already be localized (and so would sum to zero).
            var sums = scaling * (vA.LengthSquared() + vB.LengthSquared() + vC.LengthSquared());
            description.EntityShapeVolume.VolumeDistribution.M11 += sums;
            description.EntityShapeVolume.VolumeDistribution.M22 += sums;
            description.EntityShapeVolume.VolumeDistribution.M33 += sums;

            description.MinimumRadius = collisionMargin;
            description.MaximumRadius = collisionMargin + MathHelper.Max(vA.Length(), MathHelper.Max(vB.Length(), vC.Length()));
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
            FPVector3 a, b, c;

            Matrix3x3 o;
            Matrix3x3.CreateFromQuaternion(ref shapeTransform.Orientation, out o);
            Matrix3x3.Transform(ref vA, ref o, out a);
            Matrix3x3.Transform(ref vB, ref o, out b);
            Matrix3x3.Transform(ref vC, ref o, out c);

            FPVector3.Min(ref a, ref b, out boundingBox.Min);
            FPVector3.Min(ref c, ref boundingBox.Min, out boundingBox.Min);

            FPVector3.Max(ref a, ref b, out boundingBox.Max);
            FPVector3.Max(ref c, ref boundingBox.Max, out boundingBox.Max);

            boundingBox.Min.x += shapeTransform.Position.x - collisionMargin;
            boundingBox.Min.y += shapeTransform.Position.y - collisionMargin;
            boundingBox.Min.z += shapeTransform.Position.z - collisionMargin;
            boundingBox.Max.x += shapeTransform.Position.x + collisionMargin;
            boundingBox.Max.y += shapeTransform.Position.y + collisionMargin;
            boundingBox.Max.z += shapeTransform.Position.z + collisionMargin;
        }


        ///<summary>
        /// Gets the extreme point of the shape in local space in a given direction.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public override void GetLocalExtremePointWithoutMargin(ref FPVector3 direction, out FPVector3 extremePoint)
        {
            FP dotA, dotB, dotC;
            FPVector3.Dot(ref direction, ref vA, out dotA);
            FPVector3.Dot(ref direction, ref vB, out dotB);
            FPVector3.Dot(ref direction, ref vC, out dotC);
            if (dotA > dotB && dotA > dotC)
            {
                extremePoint = vA;
            }
            else if (dotB > dotC) //vA is not the most extreme point.
            {
                extremePoint = vB;
            }
            else
            {
                extremePoint = vC;
            }
        }

        /// <summary>
        /// Computes the volume distribution of the triangle.
        /// The volume distribution can be used to compute inertia tensors when
        /// paired with mass and other tuning factors.
        /// </summary>
        ///<param name="vA">First vertex in the triangle.</param>
        ///<param name="vB">Second vertex in the triangle.</param>
        ///<param name="vC">Third vertex in the triangle.</param>
        /// <returns>Volume distribution of the shape.</returns>
        public static Matrix3x3 ComputeVolumeDistribution(FPVector3 vA, FPVector3 vB, FPVector3 vC)
        {
            FPVector3 center = (vA + vB + vC) * F64.OneThird;

            //Calculate distribution of mass.

            FP massPerPoint = F64.OneThird;

            //Subtract the position from the distribution, moving into a 'body space' relative to itself.
            //        [ (j * j + z * z)  (-j * j)  (-j * z) ]
            //I = I + [ (-j * j)  (j * j + z * z)  (-j * z) ]
            //	      [ (-j * z)  (-j * z)  (j * j + j * j) ]

            FP i = vA.x - center.x;
            FP j = vA.y - center.y;
            FP k = vA.z - center.z;
            //localInertiaTensor += new Matrix(j * j + k * k, -j * j, -j * k, 0, -j * j, j * j + k * k, -j * k, 0, -j * k, -j * k, j * j + j * j, 0, 0, 0, 0, 0); //No mass per point.
            var volumeDistribution = new Matrix3x3(massPerPoint * (j * j + k * k), massPerPoint * (-i * j), massPerPoint * (-i * k),
                                                   massPerPoint * (-i * j), massPerPoint * (i * i + k * k), massPerPoint * (-j * k),
                                                   massPerPoint * (-i * k), massPerPoint * (-j * k), massPerPoint * (i * i + j * j));

            i = vB.x - center.x;
            j = vB.y - center.y;
            k = vB.z - center.z;
            var pointContribution = new Matrix3x3(massPerPoint * (j * j + k * k), massPerPoint * (-i * j), massPerPoint * (-i * k),
                                                  massPerPoint * (-i * j), massPerPoint * (i * i + k * k), massPerPoint * (-j * k),
                                                  massPerPoint * (-i * k), massPerPoint * (-j * k), massPerPoint * (i * i + j * j));
            Matrix3x3.Add(ref volumeDistribution, ref pointContribution, out volumeDistribution);

            i = vC.x - center.x;
            j = vC.y - center.y;
            k = vC.z - center.z;
            pointContribution = new Matrix3x3(massPerPoint * (j * j + k * k), massPerPoint * (-i * j), massPerPoint * (-i * k),
                                              massPerPoint * (-i * j), massPerPoint * (i * i + k * k), massPerPoint * (-j * k),
                                              massPerPoint * (-i * k), massPerPoint * (-j * k), massPerPoint * (i * i + j * j));
            Matrix3x3.Add(ref volumeDistribution, ref pointContribution, out volumeDistribution);
            return volumeDistribution;
        }

        ///<summary>
        /// Gets the normal of the triangle shape in its local space.
        ///</summary>
        ///<returns>The local normal.</returns>
        public FPVector3 GetLocalNormal()
        {
            FPVector3 normal;
            FPVector3 vAvB;
            FPVector3 vAvC;
            FPVector3.Subtract(ref vB, ref vA, out vAvB);
            FPVector3.Subtract(ref vC, ref vA, out vAvC);
            FPVector3.Cross(ref vAvB, ref vAvC, out normal);
            normal.Normalize();
            return normal;
        }

        /// <summary>
        /// Gets the normal of the triangle in world space.
        /// </summary>
        /// <param name="transform">World transform.</param>
        /// <returns>Normal of the triangle in world space.</returns>
        public FPVector3 GetNormal(RigidTransform transform)
        {
            FPVector3 normal = GetLocalNormal();
            FPQuaternion.Transform(ref normal, ref transform.Orientation, out normal);
            return normal;
        }

        /// <summary>
        /// Gets the intersection between the triangle and the ray.
        /// </summary>
        /// <param name="ray">Ray to test against the triangle.</param>
        /// <param name="transform">Transform to apply to the triangle shape for the test.</param>
        /// <param name="maximumLength">Maximum distance to travel in units of the direction vector's length.</param>
        /// <param name="hit">Hit data of the ray cast, if any.</param>
        /// <returns>Whether or not the ray hit the target.</returns>
        public override bool RayTest(ref FPRay ray, ref RigidTransform transform, FP maximumLength, out FPRayHit hit)
        {
            Matrix3x3 orientation;
            Matrix3x3.CreateFromQuaternion(ref transform.Orientation, out orientation);
            FPRay localRay;
            FPQuaternion conjugate;
            FPQuaternion.Conjugate(ref transform.Orientation, out conjugate);
            FPQuaternion.Transform(ref ray.Direction, ref conjugate, out localRay.Direction);
            FPVector3.Subtract(ref ray.Position, ref transform.Position, out localRay.Position);
            FPQuaternion.Transform(ref localRay.Position, ref conjugate, out localRay.Position);

            bool toReturn = Toolbox.FindRayTriangleIntersection(ref localRay, maximumLength, sidedness, ref vA, ref vB, ref vC, out hit);
            //Move the hit back into world space.
            FPVector3.Multiply(ref ray.Direction, hit.T, out hit.Location);
            FPVector3.Add(ref ray.Position, ref hit.Location, out hit.Location);
            FPQuaternion.Transform(ref hit.Normal, ref transform.Orientation, out hit.Normal);
            return toReturn;
        }

        /// <summary>
        /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return vA + ", " + vB + ", " + vC;
        }

        /// <summary>
        /// Retrieves an instance of an EntityCollidable that uses this EntityShape.  Mainly used by compound bodies.
        /// </summary>
        /// <returns>EntityCollidable that uses this shape.</returns>
        public override EntityCollidable GetCollidableInstance()
        {
            return new ConvexCollidable<TriangleShape>(this);
        }

    }

}
