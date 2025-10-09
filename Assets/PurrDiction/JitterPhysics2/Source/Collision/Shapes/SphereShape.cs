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
    /// Represents a sphere.
    /// </summary>
    public class SphereShape : RigidBodyShape
    {
        private Real radius;

        /// <summary>
        /// Gets or sets the radius of the sphere.
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
        /// Initializes a new instance of the <see cref="SphereShape"/> class with an optional radius parameter.
        /// The default radius is 1.0 units.
        /// </summary>
        /// <param name="radius">The radius of the sphere. Defaults to (Real)1.0.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="radius"/> is less than or equal to zero.
        /// </exception>
        public SphereShape(Real radius)
        {
            if (radius <= 0)
                throw new ArgumentOutOfRangeException(nameof(Radius), "Radius must be greater than zero.");

            this.radius = radius;
            UpdateWorldBoundingBox(0);
        }

        public override void SupportMap(in JVector direction, out JVector result)
        {
            result = JVector.Normalize(direction);
            JVector.Multiply(result, radius, out result);
        }

        public override void GetCenter(out JVector point)
        {
            point = JVector.Zero;
        }

        public override void CalculateBoundingBox(in JQuaternion orientation, in JVector position, out JBoundingBox box)
        {
            box.Min = new JVector(-radius);
            box.Max = new JVector(+radius);

            JVector.Add(box.Min, position, out box.Min);
            JVector.Add(box.Max, position, out box.Max);
        }

        public override bool LocalRayCast(in JVector origin, in JVector direction, out JVector normal, out Real lambda)
        {
            normal = JVector.Zero;
            lambda = 0.0;

            Real disq = 1.0 / direction.LengthSquared();
            Real p = JVector.Dot(direction, origin) * disq;
            Real d = p * p - (origin.LengthSquared() - radius * radius) * disq;

            if (d < 0.0) return false;

            Real sqrtd = MathR.Sqrt(d);

            Real t0 = -p - sqrtd;
            Real t1 = -p + sqrtd;

            if (t0 >= 0.0)
            {
                lambda = t0;
                JVector.Normalize(origin + t0 * direction, out normal);
                return true;
            }

            return t1 > 0.0;
        }

        public override void CalculateMassInertia(out JMatrix inertia, out JVector com, out Real mass)
        {
            mass = 4.0 / 3.0 * Real.PI * radius * radius * radius;

            // (0,0,0) is the center of mass
            inertia = JMatrix.Identity;
            inertia.M11 = 2.0 / 5.0 * mass * radius * radius;
            inertia.M22 = 2.0 / 5.0 * mass * radius * radius;
            inertia.M33 = 2.0 / 5.0 * mass * radius * radius;

            com = JVector.Zero;
        }
    }
}
