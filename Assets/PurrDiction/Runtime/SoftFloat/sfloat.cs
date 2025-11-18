
// mostly from https://github.com/CodesInChaos/SoftFloat

// Copyright (c) 2011 CodesInChaos
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// The MIT License (MIT) - http://www.opensource.org/licenses/mit-license.php
// If you need a different license please contact me

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    // Internal representation is identical to IEEE binary32 floating point numbers
    [DebuggerDisplay("{ToStringInv()}")]
    [Serializable]
    public struct sfloat : IEquatable<sfloat>, IComparable<sfloat>, IComparable, IFormattable, IPackedAuto
    {
        /// <summary>
        /// Raw byte representation of an sfloat number
        /// </summary>
        public uint rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private sfloat(uint raw)
        {
            rawValue = raw;
        }

        /// <summary>
        /// Creates an sfloat number from its raw byte representation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat FromRaw(uint raw)
        {
            return new sfloat(raw);
        }

        private uint RawMantissa { get { return rawValue & 0x7FFFFF; } }
        private int Mantissa
        {
            get
            {
                if (RawExponent != 0)
                {
                    uint sign = (uint)((int)rawValue >> 31);
                    return (int)(((RawMantissa | 0x800000) ^ sign) - sign);
                }
                else
                {
                    uint sign = (uint)((int)rawValue >> 31);
                    return (int)((RawMantissa ^ sign) - sign);
                }
            }
        }

        private sbyte Exponent => (sbyte)(RawExponent - ExponentBias);
        private byte RawExponent => (byte)(rawValue >> MantissaBits);

        private const uint SignMask = 0x80000000;
        private const int MantissaBits = 23;
        private const int ExponentBias = 127;

        private const uint RawNaN = 0xFFC00000; // Same as float.NaN
        private const uint RawPositiveInfinity = 0x7F800000;
        private const uint RawNegativeInfinity = RawPositiveInfinity ^ SignMask;
        private const uint RawOne = 0x3F800000;
        private const uint RawMinusOne = RawOne ^ SignMask;
        private const uint RawMaxValue = 0x7F7FFFFF;
        private const uint RawMinValue = 0x7F7FFFFF ^ SignMask;
        private const uint RawEpsilon = 0x00000001;


        public static sfloat zero => new sfloat(0);
        public static sfloat positiveInfinity => new sfloat(RawPositiveInfinity);
        public static sfloat negativeInfinity => new sfloat(RawNegativeInfinity);
        public static sfloat NaN => new sfloat(RawNaN);
        public static sfloat one => new sfloat(RawOne);
        public static sfloat minusOne => new sfloat(RawMinusOne);
        public static sfloat maxValue => new sfloat(RawMaxValue);
        public static sfloat minValue => new sfloat(RawMinValue);
        public static sfloat epsilon => new sfloat(RawEpsilon);

        public override string ToString() => ToFloat().ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Creates an sfloat number from its parts: sign, exponent, mantissa
        /// </summary>
        /// <param name="sign">Sign of the number: false = the number is positive, true = the number is negative</param>
        /// <param name="exponent">Exponent of the number</param>
        /// <param name="mantissa">Mantissa (significand) of the number</param>
        /// <returns></returns>
        public static sfloat FromParts(bool sign, uint exponent, uint mantissa)
        {
            return FromRaw((sign ? SignMask : 0) | ((exponent & 0xff) << MantissaBits) | (mantissa & ((1 << MantissaBits) - 1)));
        }

        /// <summary>
        /// Creates an sfloat number from a float value
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator sfloat(float f)
        {
            uint raw = ReinterpretFloatToInt32(f);
            return new sfloat(raw);
        }

        /// <summary>
        /// Converts an sfloat number to a float value
        /// </summary>
        public static explicit operator float(sfloat f) => throw new NotImplementedException();

        /// <summary>
        /// Converts an sfloat number to an integer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator int(sfloat f)
        {
            if (f.Exponent < 0)
            {
                return 0;
            }

            int shift = MantissaBits - f.Exponent;
            var mantissa = (int)(f.RawMantissa | (1 << MantissaBits));
            int value = shift < 0 ? mantissa << -shift : mantissa >> shift;
            return f.IsPositive() ? value : -value;
        }

        /// <summary>
        /// Creates an sfloat number from an integer
        /// </summary>
        public static implicit operator sfloat(int value)
        {
            switch (value)
            {
                case 0:
                    return zero;
                case int.MinValue:
                    // special case
                    return FromRaw(0xcf000000);
            }

            bool negative = value < 0;
            int u = Math.Abs(value);

            int shifts;

            int lzcnt = clz(u);
            if (lzcnt < 8)
            {
                int count = 8 - lzcnt;
                u >>= count;
                shifts = -count;
            }
            else
            {
                int count = lzcnt - 8;
                u <<= count;
                shifts = count;
            }

            uint exponent = (uint)(ExponentBias + MantissaBits - shifts);
            return FromParts(negative, exponent, (uint)u);
        }

        private static readonly int[] debruijn32 = {
            32, 8,  17, -1, -1, 14, -1, -1, -1, 20, -1, -1, -1, 28, -1, 18,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0,  26, 25, 24,
            4,  11, 23, 31, 3,  7,  10, 16, 22, 30, -1, -1, 2,  6,  13, 9,
            -1, 15, -1, 21, -1, 29, 19, -1, -1, -1, -1, -1, 1,  27, 5,  12
        };

        /// <summary>
        /// Returns the leading zero count of the given 32-bit integer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int clz(int x)
        {
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;

            return debruijn32[(uint)x * 0x8c0b2891u >> 26];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat operator -(sfloat f) => new sfloat(f.rawValue ^ 0x80000000);

        private static readonly int[] normalizeAmounts = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 8, 8, 8, 8, 8, 8, 8, 16, 16, 16, 16, 16, 16, 16, 16, 24, 24, 24, 24, 24, 24, 24
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static sfloat InternalAdd(sfloat f1, sfloat f2)
        {
            byte rawExp1 = f1.RawExponent;
            byte rawExp2 = f2.RawExponent;
            int deltaExp = rawExp1 - rawExp2;

            if (rawExp1 != 255)
            {
                //Finite
                if (deltaExp > 25)
                {
                    return f1;
                }

                int man1;
                int man2;
                if (rawExp2 != 0)
                {
                    // man1 = f1.Mantissa
                    // http://graphics.stanford.edu/~seander/bithacks.html#ConditionalNegate
                    uint sign1 = (uint)((int)f1.rawValue >> 31);
                    man1 = (int)(((f1.RawMantissa | 0x800000) ^ sign1) - sign1);
                    // man2 = f2.Mantissa
                    uint sign2 = (uint)((int)f2.rawValue >> 31);
                    man2 = (int)(((f2.RawMantissa | 0x800000) ^ sign2) - sign2);
                }
                else
                {
                    // Subnorm
                    // man2 = f2.Mantissa
                    uint sign2 = (uint)((int)f2.rawValue >> 31);
                    man2 = (int)((f2.RawMantissa ^ sign2) - sign2);

                    man1 = f1.Mantissa;

                    rawExp2 = 1;
                    if (rawExp1 == 0)
                    {
                        rawExp1 = 1;
                    }

                    deltaExp = rawExp1 - rawExp2;
                }

                int man = (man1 << 6) + ((man2 << 6) >> deltaExp);
                int absMan = Math.Abs(man);
                if (absMan == 0)
                {
                    return zero;
                }

                int rawExp = rawExp1 - 6;

                int amount = normalizeAmounts[clz(absMan)];
                rawExp -= amount;
                absMan <<= amount;

                int msbIndex = BitScanReverse8(absMan >> MantissaBits);
                rawExp += msbIndex;
                absMan >>= msbIndex;
                if ((uint)(rawExp - 1) < 254)
                {
                    uint raw = (uint)man & 0x80000000 | (uint)rawExp << MantissaBits | ((uint)absMan & 0x7FFFFF);
                    return new sfloat(raw);
                }

                switch (rawExp)
                {
                    case >= 255:
                        //Overflow
                        return man >= 0 ? positiveInfinity : negativeInfinity;
                    case >= -24:
                    {
                        uint raw = (uint)man & 0x80000000 | (uint)(absMan >> (-rawExp + 1));
                        return new sfloat(raw);
                    }
                    default:
                        return zero;
                }
            }
            // Special

            if (rawExp2 != 255)
            {
                // f1 is NaN, +Inf, -Inf and f2 is finite
                return f1;
            }

            // Both not finite
            return f1.rawValue == f2.rawValue ? f1 : NaN;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat operator +(sfloat f1, sfloat f2)
        {
            return f1.RawExponent - f2.RawExponent >= 0 ? InternalAdd(f1, f2) : InternalAdd(f2, f1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat operator -(sfloat f1, sfloat f2) => f1 + -f2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat operator *(sfloat f1, int mult)
        {
            return f1 * (sfloat)mult;
        }

        public static sfloat operator *(sfloat f1, sfloat f2)
        {
            int man1;
            int rawExp1 = f1.RawExponent;
            uint sign1;
            uint sign2;
            if (rawExp1 == 0)
            {
                // SubNorm
                sign1 = (uint)((int)f1.rawValue >> 31);
                int rawMan1 = (int)f1.RawMantissa;
                if (rawMan1 == 0)
                {
                    if (f2.IsFinite())
                    {
                        // 0 * f2
                        return new sfloat((f1.rawValue ^ f2.rawValue) & SignMask);
                    }

                    // 0 * Infinity
                    // 0 * NaN
                    return NaN;
                }

                int shift = clz(rawMan1 & 0x00ffffff) - 8;
                rawMan1 <<= shift;
                rawExp1 = 1 - shift;

                //Debug.Assert(rawMan1 >> MantissaBits == 1);
                man1 = (int)((rawMan1 ^ sign1) - sign1);
            }
            else if (rawExp1 != 255)
            {
                // Norm
                sign1 = (uint)((int)f1.rawValue >> 31);
                man1 = (int)(((f1.RawMantissa | 0x800000) ^ sign1) - sign1);
            }
            else
            {
                switch (f1.rawValue)
                {
                    // Non finite
                    case RawPositiveInfinity when f2.IsZero():
                    // Infinity * NaN
                    case RawPositiveInfinity when f2.IsNaN():
                        // Infinity * 0
                        return NaN;
                    case RawPositiveInfinity when (int)f2.rawValue >= 0:
                        // Infinity * f
                        return positiveInfinity;
                    // Infinity * -f
                    case RawPositiveInfinity:
                        return negativeInfinity;
                    case RawNegativeInfinity when f2.IsZero() || f2.IsNaN():
                        // -Infinity * 0
                        // -Infinity * NaN
                        return NaN;
                    case RawNegativeInfinity when (int)f2.rawValue < 0:
                        // -Infinity * -f
                        return positiveInfinity;
                    // -Infinity * f
                    case RawNegativeInfinity:
                        return negativeInfinity;
                    default:
                        return f1;
                }
            }

            int man2;
            int rawExp2 = f2.RawExponent;
            if (rawExp2 == 0)
            {
                // SubNorm
                sign2 = (uint)((int)f2.rawValue >> 31);
                int rawMan2 = (int)f2.RawMantissa;
                if (rawMan2 == 0)
                {
                    if (f1.IsFinite())
                    {
                        // f1 * 0
                        return new sfloat((f1.rawValue ^ f2.rawValue) & SignMask);
                    }

                    // Infinity * 0
                    // NaN * 0
                    return NaN;
                }

                int shift = clz(rawMan2 & 0x00ffffff) - 8;
                rawMan2 <<= shift;
                rawExp2 = 1 - shift;

                //Debug.Assert(rawMan2 >> MantissaBits == 1);
                man2 = (int)((rawMan2 ^ sign2) - sign2);
            }
            else if (rawExp2 != 255)
            {
                // Norm
                sign2 = (uint)((int)f2.rawValue >> 31);
                man2 = (int)(((f2.RawMantissa | 0x800000) ^ sign2) - sign2);
            }
            else
            {
                return f2.rawValue switch
                {
                    // Non finite
                    RawPositiveInfinity when f1.IsZero() => NaN,
                    RawPositiveInfinity when (int)f1.rawValue >= 0 => positiveInfinity,
                    // -f * Infinity
                    RawPositiveInfinity => negativeInfinity,
                    RawNegativeInfinity when f1.IsZero() => NaN,
                    RawNegativeInfinity when (int)f1.rawValue < 0 => positiveInfinity,
                    // f * -Infinity
                    RawNegativeInfinity => negativeInfinity,
                    _ => f2
                };
            }

            long longMan = man1 * (long)man2;
            int man = (int)(longMan >> MantissaBits);
            //Debug.Assert(man != 0);
            uint absMan = (uint)Math.Abs(man);
            int rawExp = rawExp1 + rawExp2 - ExponentBias;
            uint sign = (uint)man & 0x80000000;
            if ((absMan & 0x1000000) != 0)
            {
                absMan >>= 1;
                rawExp++;
            }

            switch (rawExp)
            {
                //Debug.Assert(absMan >> MantissaBits == 1);
                case >= 255:
                    // Overflow
                    return new sfloat(sign ^ RawPositiveInfinity);
                // Subnorms/Underflow
                case <= 0 and <= -24:
                    return new sfloat(sign);
                case <= 0:
                    absMan >>= -rawExp + 1;
                    rawExp = 0;
                    break;
            }

            uint raw = sign | (uint)rawExp << MantissaBits | absMan & 0x7FFFFF;
            return new sfloat(raw);
        }

        public static sfloat operator /(sfloat f1, sfloat f2)
        {
            if (f1.IsNaN() || f2.IsNaN())
            {
                return NaN;
            }

            int man1;
            int rawExp1 = f1.RawExponent;
            uint sign1;
            uint sign2;
            if (rawExp1 == 0)
            {
                // SubNorm
                sign1 = (uint)((int)f1.rawValue >> 31);
                int rawMan1 = (int)f1.RawMantissa;
                if (rawMan1 == 0)
                {
                    if (f2.IsZero())
                    {
                        // 0 / 0
                        return NaN;
                    }

                    // 0 / f
                    return new sfloat((f1.rawValue ^ f2.rawValue) & SignMask);
                }

                int shift = clz(rawMan1 & 0x00ffffff) - 8;
                rawMan1 <<= shift;
                rawExp1 = 1 - shift;

                //Debug.Assert(rawMan1 >> MantissaBits == 1);
                man1 = (int)((rawMan1 ^ sign1) - sign1);
            }
            else if (rawExp1 != 255)
            {
                // Norm
                sign1 = (uint)((int)f1.rawValue >> 31);
                man1 = (int)(((f1.RawMantissa | 0x800000) ^ sign1) - sign1);
            }
            else
            {
                return f1.rawValue switch
                {
                    // Non finite
                    RawPositiveInfinity when f2.IsZero() => positiveInfinity,
                    // +-Infinity / Infinity
                    RawPositiveInfinity => NaN,
                    RawNegativeInfinity when f2.IsZero() => negativeInfinity,
                    // -Infinity / +-Infinity
                    RawNegativeInfinity => NaN,
                    _ => f1
                };

                // NaN
            }

            int man2;
            int rawExp2 = f2.RawExponent;
            if (rawExp2 == 0)
            {
                // SubNorm
                sign2 = (uint)((int)f2.rawValue >> 31);
                int rawMan2 = (int)f2.RawMantissa;
                if (rawMan2 == 0)
                {
                    // f / 0
                    return new sfloat(((f1.rawValue ^ f2.rawValue) & SignMask) | RawPositiveInfinity);
                }

                int shift = clz(rawMan2 & 0x00ffffff) - 8;
                rawMan2 <<= shift;
                rawExp2 = 1 - shift;

                //Debug.Assert(rawMan2 >> MantissaBits == 1);
                man2 = (int)((rawMan2 ^ sign2) - sign2);
            }
            else if (rawExp2 != 255)
            {
                // Norm
                sign2 = (uint)((int)f2.rawValue >> 31);
                man2 = (int)(((f2.RawMantissa | 0x800000) ^ sign2) - sign2);
            }
            else
            {
                return f2.rawValue switch
                {
                    // Non finite
                    RawPositiveInfinity when f1.IsZero() => zero,
                    RawPositiveInfinity when (int)f1.rawValue >= 0 => positiveInfinity,
                    // -f / Infinity
                    RawPositiveInfinity => negativeInfinity,
                    RawNegativeInfinity when f1.IsZero() => new sfloat(SignMask),
                    RawNegativeInfinity when (int)f1.rawValue < 0 => positiveInfinity,
                    // f / -Infinity
                    RawNegativeInfinity => negativeInfinity,
                    _ => f2
                };

                // NaN
            }

            long longMan = ((long)man1 << MantissaBits) / man2;
            int man = (int)longMan;
            //Debug.Assert(man != 0);
            uint absMan = (uint)Math.Abs(man);
            int rawExp = rawExp1 - rawExp2 + ExponentBias;
            uint sign = (uint)man & 0x80000000;

            if ((absMan & 0x800000) == 0)
            {
                absMan <<= 1;
                --rawExp;
            }

            switch (rawExp)
            {
                //Debug.Assert(absMan >> MantissaBits == 1);
                case >= 255:
                    // Overflow
                    return new sfloat(sign ^ RawPositiveInfinity);
                // Subnorms/Underflow
                case <= 0 and <= -24:
                    return new sfloat(sign);
                case <= 0:
                    absMan >>= -rawExp + 1;
                    rawExp = 0;
                    break;
            }

            uint raw = sign | (uint)rawExp << MantissaBits | absMan & 0x7FFFFF;
            return new sfloat(raw);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat operator %(sfloat f1, sfloat f2) => MathS.Mod(f1, f2);

        private static readonly sbyte[] msb = {
            -1, 0, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BitScanReverse8(int b) => msb[b];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint ReinterpretFloatToInt32(float f) => *(uint*)&f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe float ReinterpretIntToFloat32(uint i) => *(float*)&i;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj != null && GetType() == obj.GetType() && Equals((sfloat)obj);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(sfloat other)
        {
            if (RawExponent != 255)
            {
                // 0 == -0
                return rawValue == other.rawValue || (rawValue & 0x7FFFFFFF) == 0 && (other.rawValue & 0x7FFFFFFF) == 0;
            }

            if (RawMantissa == 0)
            {
                // Infinities
                return rawValue == other.rawValue;
            }

            // NaNs are equal for `Equals` (as opposed to the == operator)
            return other.RawMantissa != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            if (rawValue == SignMask)
            {
                // +0 equals -0
                return 0;
            }

            if (!IsNaN())
            {
                return (int)rawValue;
            }

            // All NaNs are equal
            return unchecked((int)RawNaN);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(sfloat f1, sfloat f2)
        {
            if (f1.RawExponent != 255)
            {
                // 0 == -0
                return f1.rawValue == f2.rawValue || (f1.rawValue & 0x7FFFFFFF) == 0 && (f2.rawValue & 0x7FFFFFFF) == 0;
            }

            if (f1.RawMantissa == 0)
            {
                // Infinities
                return f1.rawValue == f2.rawValue;
            }

            //NaNs
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(sfloat f1, sfloat f2) => !(f1 == f2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(sfloat f1, sfloat f2) => !f1.IsNaN() && !f2.IsNaN() && f1.CompareTo(f2) < 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(sfloat f1, sfloat f2) => !f1.IsNaN() && !f2.IsNaN() && f1.CompareTo(f2) > 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(sfloat f1, sfloat f2) => !f1.IsNaN() && !f2.IsNaN() && f1.CompareTo(f2) <= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(sfloat f1, sfloat f2) => !f1.IsNaN() && !f2.IsNaN() && f1.CompareTo(f2) >= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(sfloat other)
        {
            if (IsNaN() && other.IsNaN())
            {
                return 0;
            }

            uint sign1 = (uint)((int)rawValue >> 31);
            int val1 = (int)((rawValue ^ (sign1 & 0x7FFFFFFF)) - sign1);

            uint sign2 = (uint)((int)other.rawValue >> 31);
            int val2 = (int)((other.rawValue ^ (sign2 & 0x7FFFFFFF)) - sign2);
            return val1.CompareTo(val2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(object obj) => obj is sfloat f ? CompareTo(f) : throw new ArgumentException("obj");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInfinity() => (rawValue & 0x7FFFFFFF) == 0x7F800000;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNegativeInfinity() => rawValue == RawNegativeInfinity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPositiveInfinity() => rawValue == RawPositiveInfinity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNaN() => RawExponent == 255 && !IsInfinity();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsFinite() => RawExponent != 255;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsZero() => (rawValue & 0x7FFFFFFF) == 0;

        public string ToString(string format, IFormatProvider formatProvider) => ToFloat().ToString(format, formatProvider);
        public string ToString(string format) => ToFloat().ToString(format);
        public string ToString(IFormatProvider provider) => ToFloat().ToString(provider);
        public string ToStringInv() => ToFloat().ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Returns the absolute value of the given sfloat number
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat Abs(sfloat f)
        {
            if (f.RawExponent != 255 || f.IsInfinity())
            {
                return new sfloat(f.rawValue & 0x7FFFFFFF);
            }

            // Leave NaN untouched
            return f;
        }

        /// <summary>
        /// Returns the maximum of the two given sfloat values. Returns NaN iff either argument is NaN.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat Max(sfloat val1, sfloat val2)
        {
            if (val1 > val2 || val1.IsNaN())
            {
                return val1;
            }

            return val2;
        }

        /// <summary>
        /// Returns the minimum of the two given sfloat values. Returns NaN iff either argument is NaN.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat Min(sfloat val1, sfloat val2)
        {
            if (val1 < val2 || val1.IsNaN())
            {
                return val1;
            }

            return val2;
        }

        /// <summary>
        /// Returns true if the sfloat number has a positive sign.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPositive() => (rawValue & 0x80000000) == 0;

        /// <summary>
        /// Returns true if the sfloat number has a negative sign.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNegative() => (rawValue & 0x80000000) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Sign()
        {
            if (IsNaN())
            {
                return 0;
            }

            if (IsZero())
            {
                return 0;
            }

            if (IsPositive())
            {
                return 1;
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sfloat FromFloat(float value)
        {
            uint raw = ReinterpretFloatToInt32(value);
            return new sfloat(raw);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat()
        {
            return ReinterpretIntToFloat32(rawValue);
        }
    }
}
