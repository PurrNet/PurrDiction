using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.sfloat;

namespace PurrNet.Prediction
{
    [System.Serializable]
    // ReSharper disable once PartialTypeWithSinglePart
    public partial struct SVec4
    {
        public Real x;
        public Real y;
        public Real z;
        public Real w;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(Real x, Real y, Real z, Real w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(SVec4 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            w = v.w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(SVec3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            w = Real.zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(SVec3 v, Real w)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(Real x, SVec3 v)
        {
            this.x = x;
            y = v.x;
            z = v.y;
            w = v.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SVec4(Real v)
        {
            x = v;
            y = v;
            z = v;
            w = v;
        }
    }
}
