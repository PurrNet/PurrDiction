using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public partial struct FPVec3
    {
        public static readonly FPVec3 zero = new FPVec3(0);
        public static readonly FPVec3 one = new FPVec3(1);
        public static readonly FPVec3 up = new FPVec3(0, 1, 0);
        public static readonly FPVec3 down = new FPVec3(0, -1, 0);
        public static readonly FPVec3 forward = new FPVec3(0, 0, 1);
        public static readonly FPVec3 back = new FPVec3(0, 0, -1);
        public static readonly FPVec3 right = new FPVec3(1, 0, 0);
        public static readonly FPVec3 left = new FPVec3(-1, 0, 0);

        public FP x;
        public FP y;
        public FP z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FP x, FP y, FP z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FPVec3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FPVec2 v, FP z)
        {
            x = v.x;
            y = v.y;
            this.z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FP x, FPVec2 v)
        {
            this.x = x;
            y = v.x;
            z = v.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FP v)
        {
            x = v;
            y = v;
            z = v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec3(FPVec2 v)
        {
            x = v.x;
            y = v.y;
            z = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
            => $"({x}, {y}, {z})";
    }
}
