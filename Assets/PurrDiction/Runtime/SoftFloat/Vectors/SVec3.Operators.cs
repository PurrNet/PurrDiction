using System.Runtime.CompilerServices;
using UnityEngine;
using Real = PurrNet.Prediction.sfloat;

namespace PurrNet.Prediction
{
    public partial struct SVec3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator +(SVec3 a, SVec3 b)
            => new SVec3(a.x + b.x, a.y + b.y, a.z + b.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator -(SVec3 a, SVec3 b)
            => new SVec3(a.x - b.x, a.y - b.y, a.z - b.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator -(SVec3 v)
            => new SVec3(-v.x, -v.y, -v.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator *(SVec3 a, Real s)
            => new SVec3(a.x * s, a.y * s, a.z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator *(Real s, SVec3 a)
            => new SVec3(a.x * s, a.y * s, a.z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator /(SVec3 a, Real s)
            => new SVec3(a.x / s, a.y / s, a.z / s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector3(SVec3 v) => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
    }
}
