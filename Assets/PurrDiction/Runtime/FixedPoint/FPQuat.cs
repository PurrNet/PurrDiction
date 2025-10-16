using System.Runtime.CompilerServices;
using UnityEngine;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public struct FPQuat
    {
        public static readonly FPQuat identity = new FPQuat(0, 0, 0, 1);

        public FP x;
        public FP y;
        public FP z;
        public FP w;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPQuat(FP x, FP y, FP z, FP w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPQuat(FPVec3 xyz, FP w)
        {
            x = xyz.x;
            y = xyz.y;
            z = xyz.z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat FromAxisAngle(FPVec3 axis, FP angleRad)
        {
            var half = angleRad * 0.5;
            var s = MathFP.Sin(half);
            var c = MathFP.Cos(half);
            return new FPQuat(axis.x * s, axis.y * s, axis.z * s, c);
        }

        public static FPQuat FromEuler(FPVec3 euler)
        {
            FP half = 0.5;
            var halfX = euler.x * half;
            var halfY = euler.y * half;
            var halfZ = euler.z * half;

            var sinX = MathFP.Sin(halfX);
            var cosX = MathFP.Cos(halfX);
            var sinY = MathFP.Sin(halfY);
            var cosY = MathFP.Cos(halfY);
            var sinZ = MathFP.Sin(halfZ);
            var cosZ = MathFP.Cos(halfZ);

            FPQuat q;
            q.x = sinX * cosY * cosZ + cosX * sinY * sinZ;
            q.y = cosX * sinY * cosZ - sinX * cosY * sinZ;
            q.z = cosX * cosY * sinZ + sinX * sinY * cosZ;
            q.w = cosX * cosY * cosZ - sinX * sinY * sinZ;
            return q;
        }

        public static FPQuat Inverse(FPQuat q)
        {
            var lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;

            if (lengthSq == FP.zero)
                return identity;

            var invLength = FP.one / lengthSq;

            return new FPQuat(
                -q.x * invLength,
                -q.y * invLength,
                -q.z * invLength,
                q.w * invLength
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat operator *(FPQuat a, FPQuat b)
        {
            return new FPQuat(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVec3 operator *(FPQuat q, FPVec3 v)
        {
            var qv = new FPVec3(q.x, q.y, q.z);
            var t = FPVec3.Cross(qv, v) * 2;
            return v + t * q.w + FPVec3.Cross(qv, t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat Conjugate(FPQuat q)
        {
            return new FPQuat(-q.x, -q.y, -q.z, q.w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat Normalize(FPQuat q)
        {
            var mag = MathFP.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return mag > FP.zero
                ? new FPQuat(q.x / mag, q.y / mag, q.z / mag, q.w / mag)
                : identity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat Lerp(FPQuat a, FPQuat b, FP t)
        {
            var q = new FPQuat(
                MathFP.Lerp(a.x, b.x, t),
                MathFP.Lerp(a.y, b.y, t),
                MathFP.Lerp(a.z, b.z, t),
                MathFP.Lerp(a.w, b.w, t)
            );
            return Normalize(q);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat Slerp(FPQuat a, FPQuat b, FP t)
        {
            var dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < FP.zero) { b = new FPQuat(-b.x, -b.y, -b.z, -b.w); dot = -dot; }

            FP threshold = 0.9995f;
            if (dot > threshold)
                return Lerp(a, b, t);

            var theta0 = MathFP.Acos(dot);
            var theta = theta0 * t;
            var sinTheta0 = MathFP.Sin(theta0);
            var sinTheta = MathFP.Sin(theta);

            var s0 = MathFP.Cos(theta) - dot * sinTheta / sinTheta0;
            var s1 = sinTheta / sinTheta0;

            var res = new FPQuat(
                a.x * s0 + b.x * s1,
                a.y * s0 + b.y * s1,
                a.z * s0 + b.z * s1,
                a.w * s0 + b.w * s1
            );
            return Normalize(res);
        }

        public override string ToString() => $"({x}, {y}, {z}, {w})";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat operator +(FPQuat a, FPQuat b)
            => new FPQuat(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPQuat operator -(FPQuat a, FPQuat b)
            => new FPQuat(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Quaternion(FPQuat v) => new Quaternion(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat(), v.w.ToFloat()).normalized;
    }
}
