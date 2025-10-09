/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using Jitter2.LinearMath;

using Real = PurrNet.Prediction.FP64;
using MathR = PurrNet.Prediction.FP64Math;

namespace Jitter2.Collision.Shapes
{
    /// <summary>
    /// Represents a shape in the form of a capsule.
    /// </summary>
    public class CapsuleShape : RigidBodyShape
    {
        private Real radius;
        private Real halfLength;

        /// <summary>
        /// Gets or sets the radius of the capsule.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="value"/> is less than or equal to zero.
        /// </exception>
        public Real Radius
        {
            get => radius;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(Radius), "Radius must be greater than zero.");
                radius = value;
                UpdateWorldBoundingBox(0);
            }
        }

        /// <summary>
        /// Gets or sets the length of the cylindrical part of the capsule, excluding the half-spheres on both ends.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="value"/> is negative.
        /// </exception>
        public Real Length
        {
            get => 2.0 * halfLength;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(Length), "Length must be greater than zero.");
                halfLength = value / 2.0;
                UpdateWorldBoundingBox(0);
            }
        }

        /// <summary>
        /// Initializes a new instance of the CapsuleShape class with the specified radius and length. The symmetry axis of the capsule is aligned along the Y-axis.
        /// </summary>
        /// <param name="radius">The radius of the capsule.</param>
        /// <param name="length">The length of the cylindrical part of the capsule, excluding the half-spheres at both ends.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="radius"/> is less than or equal to zero or when <paramref name="length"/> is negative.
        /// </exception>
        public CapsuleShape(Real radius, Real length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be greater than zero.");

            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be greater than zero.");

            this.radius = radius;
            halfLength = 0.5 * length;
            UpdateWorldBoundingBox(0);
        }

        public override void SupportMap(in JVector direction, out JVector result)
        {
            // capsule = segment + sphere

            // sphere
            result = JVector.Normalize(direction) * radius;

            // two endpoints of the segment are
            // p_1 = (0, +length/2, 0)
            // p_2 = (0, -length/2, 0)

            // we have to calculate the dot-product with the direction
            // vector to decide whether p_1 or p_2 is the correct support point
            result.Y += MathR.Sign(direction.Y) * halfLength;
        }

        public override void GetCenter(out JVector point)
        {
            point = JVector.Zero;
        }

        public override void CalculateBoundingBox(in JQuaternion orientation, in JVector position, out JBoundingBox box)
        {
            var delta = halfLength * orientation.GetBasisY();

            box.Max.X = +radius + MathR.Abs(delta.X);
            box.Max.Y = +radius + MathR.Abs(delta.Y);
            box.Max.Z = +radius + MathR.Abs(delta.Z);

            box.Min = -box.Max;

            box.Min += position;
            box.Max += position;
        }

        public override void CalculateMassInertia(out JMatrix inertia, out JVector com, out Real mass)
        {
            var length = 2.0 * halfLength;

            var massSphere = 4.0 / 3.0 * Real.PI * radius * radius * radius;
            var massCylinder = Real.PI * radius * radius * length;

            inertia = JMatrix.Identity;

            inertia.M11 = massCylinder * (1.0 / 12.0 * length * length + 1.0 / 4.0 * radius * radius) + massSphere *
                (2.0 / 5.0 * radius * radius + 1.0 / 4.0 * length * length + 3.0 / 8.0 * length * radius);
            inertia.M22 = 1.0 / 2.0 * massCylinder * radius * radius + 2.0 / 5.0 * massSphere * radius * radius;
            inertia.M33 = massCylinder * (1.0 / 12.0 * length * length + 1.0 / 4.0 * radius * radius) + massSphere *
                (2.0 / 5.0 * radius * radius + 1.0 / 4.0 * length * length + 3.0 / 8.0 * length * radius);

            mass = massCylinder + massSphere;
            com = JVector.Zero;
        }
    }
}
