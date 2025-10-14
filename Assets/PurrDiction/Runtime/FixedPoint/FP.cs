using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace PurrNet.Prediction
{
    [Serializable]
    // ReSharper disable once PartialTypeWithSinglePart
    public readonly partial struct FP : IEquatable<FP>
    {
        public const int SIZE_OF = 8;

        public readonly long rawValue;

        public static readonly FP minValue = new FP(FPMath.MinValue);
        public static readonly FP maxValue = new FP(FPMath.MaxValue);
        public static readonly FP pi = new FP(13493037705L);
        public static readonly FP zero = new FP(0);
        public static readonly FP epsilon = new FP(1L << (FPMath.Shift - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly double ToDouble() => FPMath.ToDouble(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int ToInt() => FPMath.RoundToInt(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float ToFloat() => FPMath.ToFloat(this);

        public FP(long rawValue) => this.rawValue = rawValue;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator float(FP value) => FPMath.ToFloat(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator double(FP value) => FPMath.ToDouble(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(int value) => FPMath.FromInt(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(uint value) => FPMath.FromInt((int)value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(long value) => FPMath.FromInt((int)value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(ulong value) => FPMath.FromInt((int)value);

        public static implicit operator FP(float value) => throw new NotImplementedException();

        public static implicit operator FP(double value) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator int(FP value) => value.ToInt();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator bool(FP value) => value.rawValue != FPMath.Zero;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP operand) => operand;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator ++(FP operand) => new FP(operand.rawValue + FPMath.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP operand) => new FP(FPMath.Mul(FPMath.Neg1, operand.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator --(FP operand) => new FP(operand.rawValue - FPMath.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, FP b) => new FP(FPMath.Add(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, int b) => new FP(FPMath.Add(a.rawValue, FPMath.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(int a, FP b) => new FP(FPMath.Add(FPMath.FromInt(a).rawValue, b.rawValue));

        public static FP operator +(FP a, float b) => throw new NotImplementedException();

        public static FP operator +(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, FP b) => new FP(FPMath.Sub(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, int b) => new FP(FPMath.Sub(a.rawValue, FPMath.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(int a, FP b) => new FP(FPMath.Sub(FPMath.FromInt(a).rawValue, b.rawValue));

        public static FP operator -(FP a, float b) => throw new NotImplementedException();

        public static FP operator -(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, FP b) => new FP(FPMath.Mul(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, int b) => new FP(FPMath.Mul(a.rawValue, FPMath.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(int a, FP b) => new FP(FPMath.Mul(FPMath.FromInt(a).rawValue, b.rawValue));

        public static FP operator *(FP a, float b) => throw new NotImplementedException();

        public static FP operator *(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, FP b) => new FP(FPMath.Div(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, int b) => new FP(FPMath.Div(a.rawValue, FPMath.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(int a, FP b) => new FP(FPMath.Div(FPMath.FromInt(a).rawValue, b.rawValue));

        public static FP operator /(FP a, float b) => throw new NotImplementedException();

        public static FP operator /(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, FP b) => FPMath.Mod(a, b);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, int b) => FPMath.Mod(a, FPMath.FromInt(b));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(int a, FP b) => FPMath.Mod(FPMath.FromInt(a), b);
        public static FP operator %(FP a, float b) => throw new NotImplementedException();
        public static FP operator %(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FP a, FP b) => a.rawValue == b.rawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FP a, FP b) => a.rawValue != b.rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(FP a, FP b) => a.rawValue < b.rawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(FP a, FP b) => a.rawValue > b.rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(FP a, FP b) => a.rawValue <= b.rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(FP a, FP b) => a.rawValue >= b.rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator |(FP a, FP b) => new FP(a.rawValue | b.rawValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator &(FP a, FP b) => new FP(a.rawValue & b.rawValue);

        public override string ToString() => FPMath.ToString(this);

        public bool Equals(FP other)
        {
            return rawValue == other.rawValue;
        }

        public override bool Equals(object obj)
        {
            return obj is FP other && Equals(other);
        }

        public readonly override int GetHashCode()
        {
            return (int)(rawValue ^ rawValue >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNormal(FP idet)
        {
            return idet.rawValue != FPMath.Zero;
        }

        [UsedImplicitly]
        public static FP FromRaw(long value)
        {
            return new FP(value);
        }

        [UsedImplicitly]
        public static FP FromFloat(float value)
        {
            return FPMath.FromFloatUsingBits(value);
        }
    }
}
