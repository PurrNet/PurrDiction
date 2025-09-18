using System.Runtime.CompilerServices;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public struct FP : IPackedAuto
    {
        public int rawValue;

        public int ToInt() => FPMath.RoundToInt(rawValue);

        public float ToFloat() => FPMath.ToFloat(rawValue);

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP FromRaw(int value) => new() { rawValue = value };

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static implicit operator FP(int value) => FromRaw(FPMath.FromInt(value));

        public static implicit operator FP(float value) => throw new System.NotImplementedException();

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

        public static FP operator +(FP a, float b) => throw new System.NotImplementedException();

        public static FP operator +(float a, FP b) => throw new System.NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, FP b) => FromRaw(FPMath.Sub(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(FP a, int b) => FromRaw(FPMath.Sub(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator -(int a, FP b) => FromRaw(FPMath.Sub(FPMath.FromInt(a), b.rawValue));

        public static FP operator -(FP a, float b) => throw new System.NotImplementedException();

        public static FP operator -(float a, FP b) => throw new System.NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, FP b) => FromRaw(FPMath.Mul(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(FP a, int b) => FromRaw(FPMath.Mul(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator *(int a, FP b) => FromRaw(FPMath.Mul(FPMath.FromInt(a), b.rawValue));

        public static FP operator *(FP a, float b) => throw new System.NotImplementedException();

        public static FP operator *(float a, FP b) => throw new System.NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, FP b) => FromRaw(FPMath.Div(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(FP a, int b) => FromRaw(FPMath.Div(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator /(int a, FP b) => FromRaw(FPMath.Div(FPMath.FromInt(a), b.rawValue));

        public static FP operator /(FP a, float b) => throw new System.NotImplementedException();

        public static FP operator /(float a, FP b) => throw new System.NotImplementedException();

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, FP b) => FromRaw(FPMath.Mod(a.rawValue, b.rawValue));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(FP a, int b) => FromRaw(FPMath.Mod(a.rawValue, FPMath.FromInt(b)));

        [MethodImpl(FPUtils.AggressiveInlining)]
        public static FP operator %(int a, FP b) => FromRaw(FPMath.Mod(FPMath.FromInt(a), b.rawValue));

        public static FP operator %(FP a, float b) => throw new System.NotImplementedException();

        public static FP operator %(float a, FP b) => throw new System.NotImplementedException();

        public override string ToString() => FPMath.ToString(rawValue);
    }
}
