using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.sfloat;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public partial struct SVec3
    {
        public static readonly SVec3 zero = new SVec3(Real.zero);
        public static readonly SVec3 one = new SVec3(Real.one);
        public static readonly SVec3 up = new SVec3(Real.zero, Real.one, Real.zero);
        public static readonly SVec3 down = new SVec3(Real.zero, Real.minusOne, Real.zero);
        public static readonly SVec3 forward = new SVec3(Real.zero, Real.zero, Real.one);
        public static readonly SVec3 back = new SVec3(Real.zero, Real.zero,Real.minusOne);
        public static readonly SVec3 right = new SVec3(Real.one, Real.zero, Real.zero);
        public static readonly SVec3 left = new SVec3(Real.minusOne, Real.zero, Real.zero);

        public Real x;
        public Real y;
        public Real z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(Real x, Real y, Real z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(SVec3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(SVec2 v, Real z)
        {
            x = v.x;
            y = v.y;
            this.z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(Real x, SVec2 v)
        {
            this.x = x;
            y = v.x;
            z = v.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(Real v)
        {
            x = v;
            y = v;
            z = v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec3(SVec2 v)
        {
            x = v.x;
            y = v.y;
            z = Real.zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
            => $"({x}, {y}, {z})";
    }
}
