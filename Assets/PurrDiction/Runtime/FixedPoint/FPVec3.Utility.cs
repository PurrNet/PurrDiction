using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    public partial struct FPVec3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Dot(FPVec3 a, FPVec3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 Cross(FPVec3 a, FPVec3 b)
        {
            return new FPVec3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP LengthSq(FPVec3 v)
        {
            return Dot(v, v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Length(FPVec3 v)
        {
            return MathFP.Sqrt(LengthSq(v));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 Normalize(FPVec3 v)
        {
            var len = Length(v);
            return len > FP.epsilon ? v / len : zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Distance(FPVec3 a, FPVec3 b)
        {
            return Length(a - b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 Lerp(FPVec3 a, FPVec3 b, FP t)
        {
            return a + (b - a) * t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 ComponentMin(FPVec3 a, FPVec3 b)
        {
            return new FPVec3(MathFP.Min(a.x, b.x), MathFP.Min(a.y, b.y), MathFP.Min(a.z, b.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 ComponentMax(FPVec3 a, FPVec3 b)
        {
            return new FPVec3(MathFP.Max(a.x, b.x), MathFP.Max(a.y, b.y), MathFP.Max(a.z, b.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 Abs(FPVec3 v)
        {
            return new FPVec3(MathFP.Abs(v.x), MathFP.Abs(v.y), MathFP.Abs(v.z));
        }

        // --- To FPVec2 ---
        public FPVec2 xy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(x, y); }
        public FPVec2 yx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(y, x); }
        public FPVec2 xx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(x, x); }
        public FPVec2 yy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(y, y); }

        public FPVec2 xz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(x, z); }
        public FPVec2 yz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(y, z); }
        public FPVec2 zx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(z, x); }
        public FPVec2 zy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(z, y); }
        public FPVec2 zz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec2(z, z); }

        // --- Duplicate Components ---
        public FPVec3 xxx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(x, x, x); }
        public FPVec3 yyy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(y, y, y); }
        public FPVec3 zzz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(z, z, z); }

        // --- Full Reorderings ---
        public FPVec3 xyz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(x, y, z); }
        public FPVec3 xzy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(x, z, y); }
        public FPVec3 yxz { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(y, x, z); }
        public FPVec3 yzx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(y, z, x); }
        public FPVec3 zxy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(z, x, y); }
        public FPVec3 zyx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new FPVec3(z, y, x); }
    }
}
