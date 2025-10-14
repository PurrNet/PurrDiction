using System;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct FP : IEquatable<FP>
    {
        public const int SIZE_OF = 4;

        public int rawValue;

        public static FP MinValue = FromRaw(FPMath.MinValue);

        public static FP MaxValue = FromRaw(FPMath.MaxValue);

        public int ToInt() => FPMath.RoundToInt(rawValue);

        public float ToFloat() => FPMath.ToFloat(rawValue);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP FromRaw(int value) => new() { rawValue = value };

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(int value) => FromRaw(FPMath.FromInt(value));

        public static implicit operator FP(float value) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator bool(FP value) => value.rawValue != FPMath.Zero;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP operand) => operand;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator ++(FP operand) => FromRaw(operand.rawValue + FPMath.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP operand) => FromRaw(FPMath.Mul(FPMath.Neg1, operand.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator --(FP operand) => FromRaw(operand.rawValue - FPMath.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, FP b) => FromRaw(FPMath.Add(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, int b) => FromRaw(FPMath.Add(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(int a, FP b) => FromRaw(FPMath.Add(FPMath.FromInt(a), b.rawValue));

        public static FP operator +(FP a, float b) => throw new NotImplementedException();

        public static FP operator +(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, FP b) => FromRaw(FPMath.Sub(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, int b) => FromRaw(FPMath.Sub(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(int a, FP b) => FromRaw(FPMath.Sub(FPMath.FromInt(a), b.rawValue));

        public static FP operator -(FP a, float b) => throw new NotImplementedException();

        public static FP operator -(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, FP b) => FromRaw(FPMath.Mul(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, int b) => FromRaw(FPMath.Mul(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(int a, FP b) => FromRaw(FPMath.Mul(FPMath.FromInt(a), b.rawValue));

        public static FP operator *(FP a, float b) => throw new NotImplementedException();

        public static FP operator *(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, FP b) => FromRaw(FPMath.Div(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, int b) => FromRaw(FPMath.Div(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(int a, FP b) => FromRaw(FPMath.Div(FPMath.FromInt(a), b.rawValue));

        public static FP operator /(FP a, float b) => throw new NotImplementedException();

        public static FP operator /(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, FP b) => FromRaw(FPMath.Mod(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, int b) => FromRaw(FPMath.Mod(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(int a, FP b) => FromRaw(FPMath.Mod(FPMath.FromInt(a), b.rawValue));

        public static FP operator %(FP a, float b) => throw new NotImplementedException();

        public static FP operator %(float a, FP b) => throw new NotImplementedException();

        public static bool operator ==(FP a, FP b) => a.rawValue == b.rawValue;
        public static bool operator !=(FP a, FP b) => a.rawValue != b.rawValue;

        public static bool operator <(FP a, FP b) => a.rawValue < b.rawValue;
        public static bool operator >(FP a, FP b) => a.rawValue > b.rawValue;

        public static bool operator <=(FP a, FP b) => a.rawValue <= b.rawValue;
        public static bool operator >=(FP a, FP b) => a.rawValue >= b.rawValue;

        public override string ToString() => FPMath.ToString(rawValue);

        public bool Equals(FP other)
        {
            return rawValue == other.rawValue;
        }

        public override bool Equals(object obj)
        {
            return obj is FP other && Equals(other);
        }

        public override int GetHashCode()
        {
            return rawValue;
        }
    }
}
