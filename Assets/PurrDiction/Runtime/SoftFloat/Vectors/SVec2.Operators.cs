using System.Runtime.CompilerServices;
using UnityEngine;
using Real = PurrNet.Prediction.sfloat;

namespace PurrNet.Prediction
{
    public partial struct SVec2
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 operator +(SVec2 a, SVec2 b) => new SVec2(a.x + b.x, a.y + b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 operator -(SVec2 a, SVec2 b) => new SVec2(a.x - b.x, a.y - b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 operator *(SVec2 a, Real b) => new SVec2(a.x * b, a.y * b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 operator *(Real b, SVec2 a) => new SVec2(a.x * b, a.y * b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec2 operator /(SVec2 a, Real b) => new SVec2(a.x / b, a.y / b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector2(SVec2 v) => new Vector2(v.x.ToFloat(), v.y.ToFloat());
    }
}
