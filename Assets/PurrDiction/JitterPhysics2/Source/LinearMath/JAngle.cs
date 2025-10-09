/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Runtime.InteropServices;

using Real = PurrNet.Prediction.FP64;
using MathR = PurrNet.Prediction.FP64Math;

namespace Jitter2.LinearMath
{
    /// <summary>
    /// A floating point variable of type <see cref="Real"/> representing an angle. This structure exists to eliminate
    /// ambiguity between radians and degrees in the Jitter API.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 1*Real.SIZE_OF)]
    public struct JAngle : IEquatable<JAngle>
    {
        [field: FieldOffset(0*Real.SIZE_OF)]
        public Real Radian { get; set; }

        /// <summary>
        /// Returns a string representation of the <see cref="JAngle"/>.
        /// </summary>
        public readonly override string ToString()
        {
            return $"Radian={Radian}, Degree={Degree}";
        }

        public readonly override bool Equals(object obj)
        {
            return obj is JAngle other && Equals(other);
        }

        public readonly bool Equals(JAngle p)
        {
            return p.Radian == Radian;
        }

        public readonly override int GetHashCode()
        {
            return Radian.GetHashCode();
        }

        public Real Degree
        {
            readonly get => Radian / Real.PI * 180.0;
            set => Radian = value / 180.0 * Real.PI;
        }

        public static JAngle FromRadian(Real rad)
        {
            return new JAngle { Radian = rad };
        }

        public static JAngle FromDegree(Real deg)
        {
            return new JAngle { Degree = deg };
        }

        public static explicit operator JAngle(Real angle)
        {
            return FromRadian(angle);
        }

        public static JAngle operator -(JAngle a)
        {
            return FromRadian(-a.Radian);
        }

        public static JAngle operator +(JAngle a, JAngle b)
        {
            return FromRadian(a.Radian + b.Radian);
        }

        public static JAngle operator -(JAngle a, JAngle b)
        {
            return FromRadian(a.Radian - b.Radian);
        }

        public static bool operator ==(JAngle l, JAngle r)
        {
            return (Real)l == (Real)r;
        }

        public static bool operator !=(JAngle l, JAngle r)
        {
            return (Real)l != (Real)r;
        }

        public static bool operator <(JAngle l, JAngle r)
        {
            return (Real)l < (Real)r;
        }

        public static bool operator >(JAngle l, JAngle r)
        {
            return (Real)l > (Real)r;
        }

        public static bool operator >=(JAngle l, JAngle r)
        {
            return (Real)l >= (Real)r;
        }

        public static bool operator <=(JAngle l, JAngle r)
        {
            return (Real)l <= (Real)r;
        }

        public static explicit operator Real(JAngle angle)
        {
            return angle.Radian;
        }
    }
}
