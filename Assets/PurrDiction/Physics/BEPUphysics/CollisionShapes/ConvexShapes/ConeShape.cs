using System;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.CollisionShapes.ConvexShapes
{
    ///<summary>
    /// Symmetrical shape with a circular base and a point at the top.
    ///</summary>
    public class ConeShape : ConvexShape
    {

        FP height;
        ///<summary>
        /// Gets or sets the height of the cone.
        ///</summary>
        public FP Height
        {
            get { return height; }
            set
            {
                height = value;
                OnShapeChanged();
            }
        }

        FP radius;
        ///<summary>
        /// Gets or sets the radius of the cone base.
        ///</summary>
        public FP Radius
        {
            get { return radius; }
            set
            {
                radius = value;
                OnShapeChanged();
            }
        }

        ///<summary>
        /// Constructs a new cone shape.
        ///</summary>
        ///<param name="height">Height of the cone.</param>
        ///<param name="radius">Radius of the cone base.</param>
        public ConeShape(FP height, FP radius)
        {
            this.height = height;
            this.radius = radius;

            UpdateConvexShapeInfo(ComputeDescription(height, radius, collisionMargin));
        }

        ///<summary>
        /// Constructs a new cone shape.
        ///</summary>
        ///<param name="height">Height of the cone.</param>
        ///<param name="radius">Radius of the cone base.</param>
        /// <param name="description">Cached information about the shape. Assumed to be correct; no extra processing or validation is performed.</param>
        public ConeShape(FP height, FP radius, ConvexShapeDescription description)
        {
            this.height = height;
            this.radius = radius;

            UpdateConvexShapeInfo(description);
        }


        protected override void OnShapeChanged()
        {
            UpdateConvexShapeInfo(ComputeDescription(height, radius, collisionMargin));
            base.OnShapeChanged();
        }


        /// <summary>
        /// Computes a convex shape description for a ConeShape.
        /// </summary>
        ///<param name="height">Height of the cone.</param>
        ///<param name="radius">Radius of the cone base.</param>
        ///<param name="collisionMargin">Collision margin of the shape.</param>
        /// <returns>Description required to define a convex shape.</returns>
        public static ConvexShapeDescription ComputeDescription(FP height, FP radius, FP collisionMargin)
        {
            ConvexShapeDescription description;
            description.EntityShapeVolume.Volume = F64.OneThird * MathHelper.Pi * radius * radius * height;

            description.EntityShapeVolume.VolumeDistribution = new Matrix3x3();
            FP diagValue = (F64.C0p1 * height * height + F64.C0p15 * radius * radius);
            description.EntityShapeVolume.VolumeDistribution.M11 = diagValue;
            description.EntityShapeVolume.VolumeDistribution.M22 = F64.C0p3 * radius * radius;
            description.EntityShapeVolume.VolumeDistribution.M33 = diagValue;

            description.MaximumRadius = collisionMargin + MathHelper.Max(F64.C0p75 * height, FP.Sqrt(F64.C0p0625 * height * height + radius * radius));

            FP denominator = radius / height;
            denominator = denominator / FP.Sqrt(denominator * denominator + F64.C1);
            description.MinimumRadius = collisionMargin + MathHelper.Min(F64.C0p25 * height, denominator * F64.C0p75 * height);

            description.CollisionMargin = collisionMargin;
            return description;
        }


        ///<summary>
        /// Gets the extreme point of the shape in local space in a given direction.
        ///</summary>
        ///<param name="direction">Direction to find the extreme point in.</param>
        ///<param name="extremePoint">Extreme point on the shape.</param>
        public override void GetLocalExtremePointWithoutMargin(ref FPVector3 direction, out FPVector3 extremePoint)
        {
            //Is it the tip of the cone?
            FP sinThetaSquared = radius * radius / (radius * radius + height * height);
            //If d.Y * d.Y / d.LengthSquared >= sinthetaSquared
            if (direction.Y > F64.C0 && direction.Y * direction.Y >= direction.LengthSquared() * sinThetaSquared)
            {
                extremePoint = new FPVector3(F64.C0, F64.C0p75 * height, F64.C0);
                return;
            }
            //Is it a bottom edge of the cone?
            FP horizontalLengthSquared = direction.X * direction.X + direction.Z * direction.Z;
            if (horizontalLengthSquared > Toolbox.Epsilon)
            {
                var radOverSigma = radius / FP.Sqrt(horizontalLengthSquared);
                extremePoint = new FPVector3((FP)(radOverSigma * direction.X), F64.Cm0p25 * height, (FP)(radOverSigma * direction.Z));
            }
            else // It's pointing almost straight down...
                extremePoint = new FPVector3(F64.C0, F64.Cm0p25 * height, F64.C0);


        }


        /// <summary>
        /// Retrieves an instance of an EntityCollidable that uses this EntityShape.  Mainly used by compound bodies.
        /// </summary>
        /// <returns>EntityCollidable that uses this shape.</returns>
        public override EntityCollidable GetCollidableInstance()
        {
            return new ConvexCollidable<ConeShape>(this);
        }

    }
}
