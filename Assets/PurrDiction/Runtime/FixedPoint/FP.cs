using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace PurrNet.Prediction
{
    [Serializable]
    // ReSharper disable once PartialTypeWithSinglePart
    public partial struct FP : IEquatable<FP>
    {
        public const int SIZE_OF = 8;

        public long rawValue;

        public static readonly FP minValue = new FP(MathFP.MinValue);
        public static readonly FP maxValue = new FP(MathFP.MaxValue);
        public static readonly FP pi = new FP(13493037705L);
        public static readonly FP zero = new FP(0);
        public static readonly FP one = new FP(1L << MathFP.Shift);
        public static readonly FP epsilon = new FP(1L << (MathFP.Shift - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ToDouble() => MathFP.ToDouble(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToInt() => MathFP.RoundToInt(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat() => MathFP.ToFloat(this);

        public FP(long rawValue) => this.rawValue = rawValue;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator float(FP value) => MathFP.ToFloat(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator double(FP value) => MathFP.ToDouble(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(int value) => MathFP.FromInt(value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(uint value) => MathFP.FromInt((int)value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(long value) => MathFP.FromInt((int)value);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(ulong value) => MathFP.FromInt((int)value);

        public static implicit operator FP(float value) => throw new NotImplementedException();

        public static implicit operator FP(double value) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static explicit operator int(FP value) => value.ToInt();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator bool(FP value) => value.rawValue != MathFP.Zero;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP operand) => operand;

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator ++(FP operand) => new FP(operand.rawValue + MathFP.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP operand) => new FP(MathFP.Mul(MathFP.Neg1, operand.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator --(FP operand) => new FP(operand.rawValue - MathFP.One);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, FP b) => new FP(MathFP.Add(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(FP a, int b) => new FP(MathFP.Add(a.rawValue, MathFP.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator +(int a, FP b) => new FP(MathFP.Add(MathFP.FromInt(a).rawValue, b.rawValue));

        public static FP operator +(FP a, float b) => throw new NotImplementedException();

        public static FP operator +(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, FP b) => new FP(MathFP.Sub(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, int b) => new FP(MathFP.Sub(a.rawValue, MathFP.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(int a, FP b) => new FP(MathFP.Sub(MathFP.FromInt(a).rawValue, b.rawValue));

        public static FP operator -(FP a, float b) => throw new NotImplementedException();

        public static FP operator -(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, FP b) => new FP(MathFP.Mul(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, int b) => new FP(MathFP.Mul(a.rawValue, MathFP.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(int a, FP b) => new FP(MathFP.Mul(MathFP.FromInt(a).rawValue, b.rawValue));

        public static FP operator *(FP a, float b) => throw new NotImplementedException();

        public static FP operator *(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, FP b) => new FP(MathFP.Div(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, int b) => new FP(MathFP.Div(a.rawValue, MathFP.FromInt(b).rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(int a, FP b) => new FP(MathFP.Div(MathFP.FromInt(a).rawValue, b.rawValue));

        public static FP operator /(FP a, float b) => throw new NotImplementedException();

        public static FP operator /(float a, FP b) => throw new NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, FP b) => MathFP.Mod(a, b);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, int b) => MathFP.Mod(a, MathFP.FromInt(b));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(int a, FP b) => MathFP.Mod(MathFP.FromInt(a), b);
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

        public override string ToString() => MathFP.ToString(this);

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
            return (int)(rawValue ^ rawValue >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNormal(FP idet)
        {
            return idet.rawValue != MathFP.Zero;
        }

        [UsedImplicitly]
        public static FP FromRaw(long value)
        {
            return new FP(value);
        }

        [UsedImplicitly]
        public static FP FromFloat(float value)
        {
            return MathFP.FromFloatUsingBits(value);
        }
    }
}
