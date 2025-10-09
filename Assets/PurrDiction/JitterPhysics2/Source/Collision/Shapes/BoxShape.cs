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
    /// Represents a three-dimensional box shape.
    /// </summary>
    public class BoxShape : RigidBodyShape
    {
        private JVector halfSize;

        /// <summary>
        /// Creates a box shape with specified dimensions.
        /// </summary>
        /// <param name="size">The dimensions of the box.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when any component of <paramref name="size"/> is less than or equal to zero.
        /// </exception>
        public BoxShape(JVector size)
        {
            if (size.X <= 0)
                throw new ArgumentOutOfRangeException(nameof(size), "The x-component of the size vector must be greater than zero.");
            if (size.Y <= 0)
                throw new ArgumentOutOfRangeException(nameof(size), "The y-component of the size vector must be greater than zero.");
            if (size.Z <= 0)
                throw new ArgumentOutOfRangeException(nameof(size), "The z-component of the size vector must be greater than zero.");

            halfSize = 0.5 * size;
            UpdateWorldBoundingBox(0);
        }

        /// <summary>
        /// Gets or sets the dimensions of the box.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when any component of <paramref name="value"/> is less than or equal to zero.
        /// </exception>
        public JVector Size
        {
            get => 2.0 * halfSize;
            set
            {
                if (value.X <= 0)
                    throw new ArgumentOutOfRangeException(nameof(Size), "The x-component of the size vector must be greater than zero.");
                if (value.Y <= 0)
                    throw new ArgumentOutOfRangeException(nameof(Size), "The y-component of the size vector must be greater than zero.");
                if (value.Z <= 0)
                    throw new ArgumentOutOfRangeException(nameof(Size), "The z-component of the size vector must be greater than zero.");


                halfSize = value * 0.5f;
                UpdateWorldBoundingBox(0);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="size">The length of each side.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="size"/> is less than or equal to zero.
        /// </exception>
        public BoxShape(Real size)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size), "The size must be greater than zero.");

            halfSize = new JVector(size * 0.5);
            UpdateWorldBoundingBox(0);
        }

        /// <summary>
        /// Creates a box shape with the specified length, height, and width.
        /// </summary>
        /// <param name="length">The length of the box.</param>
        /// <param name="height">The height of the box.</param>
        /// <param name="width">The width of the box.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="length"/>, <paramref name="height"/>, or <paramref name="width"/> is less than
        /// or equal to zero.
        /// </exception>
        public BoxShape(Real width, Real height, Real length)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "The width must be greater than zero.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), "The height must be greater than zero.");
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length), "The length must be greater than zero.");

            halfSize = 0.5 * new JVector(width, height, length);
            UpdateWorldBoundingBox(0);
        }

        public override void SupportMap(in JVector direction, out JVector result)
        {
            result.X = MathHelper.SignBit(direction.X) * halfSize.X;
            result.Y = MathHelper.SignBit(direction.Y) * halfSize.Y;
            result.Z = MathHelper.SignBit(direction.Z) * halfSize.Z;
        }

        public override bool LocalRayCast(in JVector origin, in JVector direction, out JVector normal, out Real lambda)
        {
            Real epsilon = 1e-22;

            JVector min = -halfSize;
            JVector max = halfSize;

            normal = JVector.Zero;
            lambda = 0.0;

            Real exit = Real.MaxValue;

            if (MathR.Abs(direction.X) > epsilon)
            {
                Real ix = 1.0 / direction.X;
                Real t0 = (min.X - origin.X) * ix;
                Real t1 = (max.X - origin.X) * ix;

                if (t0 > t1) (t0, t1) = (t1, t0);

                if (t0 > exit || t1 < lambda) return false;

                if (t0 > lambda)
                {
                    lambda = t0;
                    normal = direction.X < 0.0 ? JVector.UnitX : -JVector.UnitX;
                }

                if (t1 < exit) exit = t1;
            }
            else if (origin.X < min.X || origin.X > max.X)
            {
                return false;
            }

            if (MathR.Abs(direction.Y) > epsilon)
            {
                Real iy = 1.0 / direction.Y;
                Real t0 = (min.Y - origin.Y) * iy;
                Real t1 = (max.Y - origin.Y) * iy;

                if (t0 > t1) (t0, t1) = (t1, t0);

                if (t0 > exit || t1 < lambda) return false;

                if (t0 > lambda)
                {
                    lambda = t0;
                    normal = direction.Y < 0.0 ? JVector.UnitY : -JVector.UnitY;
                }

                if (t1 < exit) exit = t1;
            }
            else if (origin.Y < min.Y || origin.Y > max.Y)
            {
                return false;
            }

            if (MathR.Abs(direction.Z) > epsilon)
            {
                Real iz = 1.0 / direction.Z;
                Real t0 = (min.Z - origin.Z) * iz;
                Real t1 = (max.Z - origin.Z) * iz;

                if (t0 > t1) (t0, t1) = (t1, t0);

                if (t0 > exit || t1 < lambda) return false;

                if (t0 > lambda)
                {
                    lambda = t0;
                    normal = direction.Z < 0.0 ? JVector.UnitZ : -JVector.UnitZ;
                }
                //if (t1 < exit) exit = t1;
            }
            else if (origin.Z < min.Z || origin.Z > max.Z)
            {
                return false;
            }

            return true;
        }

        public override void GetCenter(out JVector point)
        {
            point = JVector.Zero;
        }

        public override void CalculateBoundingBox(in JQuaternion orientation, in JVector position, out JBoundingBox box)
        {
            JMatrix.Absolute(JMatrix.CreateFromQuaternion(orientation), out JMatrix absm);
            var ths = JVector.Transform(halfSize, absm);
            box.Min = position - ths;
            box.Max = position + ths;
        }

        public override void CalculateMassInertia(out JMatrix inertia, out JVector com, out Real mass)
        {
            JVector size = halfSize * 2.0;
            mass = size.X * size.Y * size.Z;

            inertia = JMatrix.Identity;
            inertia.M11 = 1.0 / 12.0 * mass * (size.Y * size.Y + size.Z * size.Z);
            inertia.M22 = 1.0 / 12.0 * mass * (size.X * size.X + size.Z * size.Z);
            inertia.M33 = 1.0 / 12.0 * mass * (size.X * size.X + size.Y * size.Y);

            com = JVector.Zero;
        }
    }
}
