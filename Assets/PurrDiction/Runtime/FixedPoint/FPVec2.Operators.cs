using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    public partial struct FPVec2
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 operator +(FPVec2 a, FPVec2 b) => new FPVec2(a.x + b.x, a.y + b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 operator -(FPVec2 a, FPVec2 b) => new FPVec2(a.x - b.x, a.y - b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 operator *(FPVec2 a, FP b) => new FPVec2(a.x * b, a.y * b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 operator *(FP b, FPVec2 a) => new FPVec2(a.x * b, a.y * b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec2 operator /(FPVec2 a, FP b) => new FPVec2(a.x / b, a.y / b);
    }
}
