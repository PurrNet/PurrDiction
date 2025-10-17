using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    [System.Serializable]
    // ReSharper disable once PartialTypeWithSinglePart
    public partial struct FPVec4
    {
        public FP x;
        public FP y;
        public FP z;
        public FP w;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FP x, FP y, FP z, FP w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FPVec4 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            w = v.w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FPVec3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            w = FP.zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FPVec3 v, FP w)
        {
            x = v.x;
            y = v.y;
            z = v.z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FP x, FPVec3 v)
        {
            this.x = x;
            y = v.x;
            z = v.y;
            w = v.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec4(FP v)
        {
            x = v;
            y = v;
            z = v;
            w = v;
        }
    }
}
