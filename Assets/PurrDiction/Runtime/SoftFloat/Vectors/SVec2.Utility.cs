using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.sfloat;
using Math = PurrNet.Prediction.MathS;

namespace PurrNet.Prediction
{
    public partial struct SVec2
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Dot(SVec2 a, SVec2 b)
        {
            return a.x * b.x + a.y * b.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Cross(SVec2 a, SVec2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real LengthSq(SVec2 v)
        {
            return Dot(v, v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Length(SVec2 v)
        {
            return Math.Sqrt(LengthSq(v));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 Normalize(SVec2 v)
        {
            var mag = Length(v);
            return mag > Real.zero ? v / mag : new SVec2(Real.zero, Real.zero);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Distance(SVec2 a, SVec2 b)
        {
            return Length(a - b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 Lerp(SVec2 a, SVec2 b, Real t)
        {
            return a + (b - a) * t;
        }

        public SVec2 xy => new SVec2(x, y);
        public SVec2 yx => new SVec2(y, x);
        public SVec2 xx => new SVec2(x, x);
        public SVec2 yy => new SVec2(y, y);
    }
}
