using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public partial struct FPVec2
    {
        public static readonly FPVec2 zero = new FPVec2(FP.zero, FP.zero);
        public static readonly FPVec2 one = new FPVec2(1, 1);
        public static readonly FPVec2 up = new FPVec2(FP.zero, 1);
        public static readonly FPVec2 right = new FPVec2(1, FP.zero);

        public FP x;
        public FP y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec2(FP x, FP y)
        {
            this.x = x;
            this.y = y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec2(FPVec2 v)
        {
            x = v.x;
            y = v.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVec2(FP v)
        {
            x = v;
            y = v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
            => $"({x}, {y})";
    }
}
