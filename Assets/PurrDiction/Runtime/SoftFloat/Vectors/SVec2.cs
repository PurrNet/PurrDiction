using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.sfloat;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public partial struct SVec2
    {
        public static readonly SVec2 zero = new SVec2(Real.zero, Real.zero);
        public static readonly SVec2 one = new SVec2(Real.one, Real.one);
        public static readonly SVec2 up = new SVec2(Real.zero, Real.one);
        public static readonly SVec2 right = new SVec2(Real.one, Real.zero);

        public Real x;
        public Real y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec2(Real x, Real y)
        {
            this.x = x;
            this.y = y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec2(SVec2 v)
        {
            x = v.x;
            y = v.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec2(Real v)
        {
            x = v;
            y = v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
            => $"({x}, {y})";
    }
}
