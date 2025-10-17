using System.Runtime.CompilerServices;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial struct FPVec3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator +(FPVec3 a, FPVec3 b)
            => new FPVec3(a.x + b.x, a.y + b.y, a.z + b.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator -(FPVec3 a, FPVec3 b)
            => new FPVec3(a.x - b.x, a.y - b.y, a.z - b.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator -(FPVec3 v)
            => new FPVec3(-v.x, -v.y, -v.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator *(FPVec3 a, FP s)
            => new FPVec3(a.x * s, a.y * s, a.z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator *(FP s, FPVec3 a)
            => new FPVec3(a.x * s, a.y * s, a.z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator /(FPVec3 a, FP s)
            => new FPVec3(a.x / s, a.y / s, a.z / s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector3(FPVec3 v) => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
    }
}
