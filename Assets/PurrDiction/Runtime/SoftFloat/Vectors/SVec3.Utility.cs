using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.sfloat;
using Math = PurrNet.Prediction.MathS;

namespace PurrNet.Prediction
{
    public partial struct SVec3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Dot(SVec3 a, SVec3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 Cross(SVec3 a, SVec3 b)
        {
            return new SVec3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real LengthSq(SVec3 v)
        {
            return Dot(v, v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Length(SVec3 v)
        {
            return Math.Sqrt(LengthSq(v));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 Normalize(SVec3 v)
        {
            var len = Length(v);
            return len > Real.epsilon ? v / len : zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Real Distance(SVec3 a, SVec3 b)
        {
            return Length(a - b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 Lerp(SVec3 a, SVec3 b, Real t)
        {
            return a + (b - a) * t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 ComponentMin(SVec3 a, SVec3 b)
        {
            return new SVec3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 ComponentMax(SVec3 a, SVec3 b)
        {
            return new SVec3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 Abs(SVec3 v)
        {
            return new SVec3(Math.Abs(v.x), Math.Abs(v.y), Math.Abs(v.z));
        }

        // --- To FPVec2 ---
        public SVec2 xy => new SVec2(x, y);
        public SVec2 yx => new SVec2(y, x);
        public SVec2 xx => new SVec2(x, x);
        public SVec2 yy => new SVec2(y, y);

        public SVec2 xz => new SVec2(x, z);
        public SVec2 yz => new SVec2(y, z);
        public SVec2 zx => new SVec2(z, x);
        public SVec2 zy => new SVec2(z, y);
        public SVec2 zz => new SVec2(z, z);

        // --- Duplicate Components ---
        public SVec3 xxx => new SVec3(x, x, x);
        public SVec3 yyy => new SVec3(y, y, y);
        public SVec3 zzz => new SVec3(z, z, z);

        // --- Full Reorderings ---
        public SVec3 xyz => new SVec3(x, y, z);
        public SVec3 xzy => new SVec3(x, z, y);
        public SVec3 yxz => new SVec3(y, x, z);
        public SVec3 yzx => new SVec3(y, z, x);
        public SVec3 zxy => new SVec3(z, x, y);
        public SVec3 zyx => new SVec3(z, y, x);
    }
}
