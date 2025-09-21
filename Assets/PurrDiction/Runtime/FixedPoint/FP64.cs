using System;
using System.Runtime.CompilerServices;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct FP64 : IPackedAuto, IEquatable<FP64>
    {
        public long rawValue;

        public double ToDouble() => FP64Math.ToDouble(rawValue);

        public int ToInt() => FP64Math.RoundToInt(rawValue);

        public float ToFloat() => FP64Math.ToFloat(rawValue);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 FromRaw(long value) => new() { rawValue = value };

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP64(int value) => FromRaw(FP64Math.FromInt(value));

        public static implicit operator FP64(float value) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator bool(FP64 value) => value.rawValue != FP64Math.Zero;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator +(FP64 operand) => operand;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator ++(FP64 operand) => FromRaw(operand.rawValue + FP64Math.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator -(FP64 operand) => FromRaw(FP64Math.Mul(FP64Math.Neg1, operand.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator --(FP64 operand) => FromRaw(operand.rawValue - FP64Math.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator +(FP64 a, FP64 b) => FromRaw(FP64Math.Add(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator +(FP64 a, int b) => FromRaw(FP64Math.Add(a.rawValue, FP64Math.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator +(int a, FP64 b) => FromRaw(FP64Math.Add(FP64Math.FromInt(a), b.rawValue));

        public static FP64 operator +(FP64 a, float b) => throw new NotImplementedException();

        public static FP64 operator +(float a, FP64 b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator -(FP64 a, FP64 b) => FromRaw(FP64Math.Sub(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator -(FP64 a, int b) => FromRaw(FP64Math.Sub(a.rawValue, FP64Math.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator -(int a, FP64 b) => FromRaw(FP64Math.Sub(FP64Math.FromInt(a), b.rawValue));

        public static FP64 operator -(FP64 a, float b) => throw new NotImplementedException();

        public static FP64 operator -(float a, FP64 b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator *(FP64 a, FP64 b) => FromRaw(FP64Math.Mul(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator *(FP64 a, int b) => FromRaw(FP64Math.Mul(a.rawValue, FP64Math.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator *(int a, FP64 b) => FromRaw(FP64Math.Mul(FP64Math.FromInt(a), b.rawValue));

        public static FP64 operator *(FP64 a, float b) => throw new NotImplementedException();

        public static FP64 operator *(float a, FP64 b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator /(FP64 a, FP64 b) => FromRaw(FP64Math.Div(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator /(FP64 a, int b) => FromRaw(FP64Math.Div(a.rawValue, FP64Math.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator /(int a, FP64 b) => FromRaw(FP64Math.Div(FP64Math.FromInt(a), b.rawValue));

        public static FP64 operator /(FP64 a, float b) => throw new NotImplementedException();

        public static FP64 operator /(float a, FP64 b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator %(FP64 a, FP64 b) => FromRaw(FP64Math.Mod(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator %(FP64 a, int b) => FromRaw(FP64Math.Mod(a.rawValue, FP64Math.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP64 operator %(int a, FP64 b) => FromRaw(FP64Math.Mod(FP64Math.FromInt(a), b.rawValue));

        public static FP64 operator %(FP64 a, float b) => throw new NotImplementedException();

        public static FP64 operator %(float a, FP64 b) => throw new NotImplementedException();

        public static bool operator ==(FP64 a, FP64 b) => a.rawValue == b.rawValue;
        public static bool operator !=(FP64 a, FP64 b) => a.rawValue != b.rawValue;

        public static bool operator <(FP64 a, FP64 b) => a.rawValue < b.rawValue;
        public static bool operator >(FP64 a, FP64 b) => a.rawValue > b.rawValue;

        public static bool operator <=(FP64 a, FP64 b) => a.rawValue <= b.rawValue;
        public static bool operator >=(FP64 a, FP64 b) => a.rawValue >= b.rawValue;

        public override string ToString() => FP64Math.ToString(rawValue);

        public bool Equals(FP64 other)
        {
            return rawValue == other.rawValue;
        }

        public override bool Equals(object obj)
        {
            return obj is FP64 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)(rawValue ^ rawValue >> 32);
        }
    }
}
