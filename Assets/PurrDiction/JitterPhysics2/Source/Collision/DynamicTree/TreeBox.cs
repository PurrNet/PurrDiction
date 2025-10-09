/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitter2.LinearMath;

using Real = PurrNet.Prediction.FP64;

namespace Jitter2.Collision
{
    /// <summary>
    /// Represents an axis-aligned bounding box with SIMD-friendly memory layout, used for spatial partitioning in
    /// acceleration structures such as <see cref="DynamicTree"/>.
    /// </summary>
    /// <remarks>
    /// The struct is explicitly laid out to occupy exactly 8 <see cref="Real"/> fields, enabling efficient SIMD operations and
    /// binary comparisons. It contains both minimum and maximum corners of the box as <see cref="JVector"/>, along with
    /// unused W components (<c>MinW</c>, <c>MaxW</c>) to match memory alignment.
    /// </remarks>
    /// <seealso cref="JBoundingBox"/>
    /// <seealso cref="JVector"/>
    public struct TreeBox : IEquatable<TreeBox>
    {
        public static readonly Real Epsilon = 1e-8;

        public JVector Min;
        public JVector Max;

        public TreeBox(in JVector min, in JVector max)
        {
            this.Min = min;
            this.Max = max;
        }

        public TreeBox(in JBoundingBox box)
        {
            this.Min = box.Min;
            this.Max = box.Max;
        }

        public readonly JBoundingBox AsJBoundingBox() => new(Min, Max);

        // ─── Helper functions 1:1 like in JBox ───────────────────────────

        public readonly bool Contains(in JVector point)
        {
            return Min.X <= point.X && point.X <= Max.X &&
                   Min.Y <= point.Y && point.Y <= Max.Y &&
                   Min.Z <= point.Z && point.Z <= Max.Z;
        }

        public readonly JVector Center => (Min + Max) * (1.0 / 2.0);

        public readonly Real GetVolume()
        {
            JVector len = Max - Min;
            return len.X * len.Y * len.Z;
        }

        public readonly Real GetSurfaceArea()
        {
            JVector len = Max - Min;
            return 2.0 * (len.X * len.Y + len.Y * len.Z + len.Z * len.X);
        }

        public readonly bool NotDisjoint(in JBoundingBox box)
        {
            return Max.X >= box.Min.X && Min.X <= box.Max.X && Max.Y >= box.Min.Y && Min.Y <= box.Max.Y &&
                   Max.Z >= box.Min.Z && Min.Z <= box.Max.Z;
        }

        public readonly bool Disjoint(in JBoundingBox box)
        {
            return Max.X < box.Min.X || Min.X > box.Max.X || Max.Y < box.Min.Y || Min.Y > box.Max.Y ||
                   Max.Z < box.Min.Z || Min.Z > box.Max.Z;
        }

        public readonly bool Encompasses(in JBoundingBox box)
        {
            return Min.X <= box.Min.X && Max.X >= box.Max.X && Min.Y <= box.Min.Y && Max.Y >= box.Max.Y &&
                   Min.Z <= box.Min.Z && Max.Z >= box.Max.Z;
        }

        private static bool Intersect1D(Real start, Real dir, Real min, Real max,
            ref Real enter, ref Real exit)
        {
            if (dir * dir < Epsilon * Epsilon) return start >= min && start <= max;

            Real t0 = (min - start) / dir;
            Real t1 = (max - start) / dir;

            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            if (t0 > exit || t1 < enter) return false;

            if (t0 > enter) enter = t0;
            if (t1 < exit) exit = t1;
            return true;
        }

        public readonly bool SegmentIntersect(in JVector origin, in JVector direction)
        {
            Real enter = 0.0, exit = 1.0;

            if (!Intersect1D(origin.X, direction.X, Min.X, Max.X, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Y, direction.Y, Min.Y, Max.Y, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Z, direction.Z, Min.Z, Max.Z, ref enter, ref exit))
                return false;

            return true;
        }

        public readonly bool RayIntersect(in JVector origin, in JVector direction)
        {
            Real enter = 0.0, exit = Real.MaxValue;

            if (!Intersect1D(origin.X, direction.X, Min.X, Max.X, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Y, direction.Y, Min.Y, Max.Y, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Z, direction.Z, Min.Z, Max.Z, ref enter, ref exit))
                return false;

            return true;
        }

        public readonly bool RayIntersect(in JVector origin, in JVector direction, out Real enter)
        {
            enter = 0.0;
            Real exit = Real.MaxValue;

            if (!Intersect1D(origin.X, direction.X, Min.X, Max.X, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Y, direction.Y, Min.Y, Max.Y, ref enter, ref exit))
                return false;

            if (!Intersect1D(origin.Z, direction.Z, Min.Z, Max.Z, ref enter, ref exit))
                return false;

            return true;
        }

        public readonly override string ToString()
        {
            return $"Min={{{Min}}}, Max={{{Max}}}";
        }

        // ─── Helper functions with SIMD support ───────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real MergedSurface(in TreeBox first, in TreeBox second)
        {
            var vMin = JVector.Min(first.Min, second.Min);
            var vMax = JVector.Max(first.Max, second.Max);
            var extent = JVector.Subtract(vMax, vMin);

            var ex = extent.X;
            var ey = extent.Y;
            var ez = extent.Z;

            return 2.0 * (ex * ey + ex * ez + ey * ez);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Encompasses(in TreeBox outer, in TreeBox inner)
        {
            return outer.Min.X <= inner.Min.X &&
                   outer.Min.Y <= inner.Min.Y &&
                   outer.Min.Z <= inner.Min.Z &&
                   outer.Max.X >= inner.Max.X &&
                   outer.Max.Y >= inner.Max.Y &&
                   outer.Max.Z >= inner.Max.Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool NotDisjoint(in TreeBox a, in TreeBox b)
        {
            return a.Max.X >= b.Min.X &&
                   a.Min.X <= b.Max.X &&
                   a.Max.Y >= b.Min.Y &&
                   a.Min.Y <= b.Max.Y &&
                   a.Max.Z >= b.Min.Z &&
                   a.Min.Z <= b.Max.Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CreateMerged(in TreeBox first, in TreeBox second, out TreeBox result)
        {
            Unsafe.SkipInit(out result);
            result.Min = JVector.Min(first.Min, second.Min);
            result.Max = JVector.Max(first.Max, second.Max);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Equals(in TreeBox first, in TreeBox second)
        {
            var a = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first), 1));
            var b = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in second), 1));
            return a.SequenceEqual(b); // SIMD-accelerated in .NET ≥ 5
        }

        public readonly bool Equals(TreeBox other)
        {
            return Equals(this, other);
        }

        public readonly override bool Equals(object obj)
        {
            return obj is TreeBox other && Equals(other);
        }

        public readonly override int GetHashCode()
        {
            return HashCode.Combine(Min, Max);
        }

        public static bool operator ==(TreeBox left, TreeBox right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TreeBox left, TreeBox right)
        {
            return !(left == right);
        }
    }
}
