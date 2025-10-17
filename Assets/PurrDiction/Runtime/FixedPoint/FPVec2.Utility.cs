using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    public partial struct FPVec2
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Dot(FPVec2 a, FPVec2 b)
        {
            return a.x * b.x + a.y * b.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Cross(FPVec2 a, FPVec2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP LengthSq(FPVec2 v)
        {
            return Dot(v, v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Length(FPVec2 v)
        {
            return MathFP.Sqrt(LengthSq(v));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 Normalize(FPVec2 v)
        {
            var mag = Length(v);
            return mag > FP.zero ? v / mag : new FPVec2(FP.zero, FP.zero);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Distance(FPVec2 a, FPVec2 b)
        {
            return Length(a - b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 Lerp(FPVec2 a, FPVec2 b, FP t)
        {
            return a + (b - a) * t;
        }

        public FPVec2 xy => new FPVec2(x, y);
        public FPVec2 yx => new FPVec2(y, x);
        public FPVec2 xx => new FPVec2(x, x);
        public FPVec2 yy => new FPVec2(y, y);
    }
}
