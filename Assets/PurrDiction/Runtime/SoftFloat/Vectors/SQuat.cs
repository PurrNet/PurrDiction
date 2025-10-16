using System.Runtime.CompilerServices;
using UnityEngine;
using Real = PurrNet.Prediction.sfloat;
using Math = PurrNet.Prediction.MathS;

namespace PurrNet.Prediction
{
    [System.Serializable]
    public struct SQuat
    {
        public static readonly SQuat identity = new SQuat(0, 0, 0, 1);

        public Real x;
        public Real y;
        public Real z;
        public Real w;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SQuat(Real x, Real y, Real z, Real w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SQuat(SVec3 xyz, Real w)
        {
            x = xyz.x;
            y = xyz.y;
            z = xyz.z;
            this.w = w;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat FromAxisAngle(SVec3 axis, Real angleRad)
        {
            var half = angleRad * 0.5f;
            var s = Math.Sin(half);
            var c = Math.Cos(half);
            return new SQuat(axis.x * s, axis.y * s, axis.z * s, c);
        }

        public static SQuat FromEuler(SVec3 euler)
        {
            var half = (Real)0.5;
            var halfX = euler.x * half;
            var halfY = euler.y * half;
            var halfZ = euler.z * half;

            var sinX = Math.Sin(halfX);
            var cosX = Math.Cos(halfX);
            var sinY = Math.Sin(halfY);
            var cosY = Math.Cos(halfY);
            var sinZ = Math.Sin(halfZ);
            var cosZ = Math.Cos(halfZ);

            SQuat q;
            q.x = sinX * cosY * cosZ + cosX * sinY * sinZ;
            q.y = cosX * sinY * cosZ - sinX * cosY * sinZ;
            q.z = cosX * cosY * sinZ + sinX * sinY * cosZ;
            q.w = cosX * cosY * cosZ - sinX * sinY * sinZ;
            return q;
        }

        public static SQuat Inverse(SQuat q)
        {
            var lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;

            if (lengthSq == Real.zero)
                return identity;

            var invLength = Real.one / lengthSq;

            return new SQuat(
                -q.x * invLength,
                -q.y * invLength,
                -q.z * invLength,
                q.w * invLength
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat operator *(SQuat a, SQuat b)
        {
            return new SQuat(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SVec3 operator *(SQuat q, SVec3 v)
        {
            var qv = new SVec3(q.x, q.y, q.z);
            var t = SVec3.Cross(qv, v) * 2;
            return v + t * q.w + SVec3.Cross(qv, t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat Conjugate(SQuat q)
        {
            return new SQuat(-q.x, -q.y, -q.z, q.w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat Normalize(SQuat q)
        {
            var mag = Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return mag > Real.zero
                ? new SQuat(q.x / mag, q.y / mag, q.z / mag, q.w / mag)
                : identity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat Lerp(SQuat a, SQuat b, Real t)
        {
            var q = new SQuat(
                Math.Lerp(a.x, b.x, t),
                Math.Lerp(a.y, b.y, t),
                Math.Lerp(a.z, b.z, t),
                Math.Lerp(a.w, b.w, t)
            );
            return Normalize(q);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat Slerp(SQuat a, SQuat b, Real t)
        {
            var dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < Real.zero) { b = new SQuat(-b.x, -b.y, -b.z, -b.w); dot = -dot; }

            var threshold = (Real)0.9995f;
            if (dot > threshold)
                return Lerp(a, b, t);

            var theta0 = Math.Acos(dot);
            var theta = theta0 * t;
            var sinTheta0 = Math.Sin(theta0);
            var sinTheta = Math.Sin(theta);

            var s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
            var s1 = sinTheta / sinTheta0;

            var res = new SQuat(
                a.x * s0 + b.x * s1,
                a.y * s0 + b.y * s1,
                a.z * s0 + b.z * s1,
                a.w * s0 + b.w * s1
            );
            return Normalize(res);
        }

        public override string ToString() => $"({x}, {y}, {z}, {w})";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat operator +(SQuat a, SQuat b)
            => new SQuat(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SQuat operator -(SQuat a, SQuat b)
            => new SQuat(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Quaternion(SQuat v) => new Quaternion(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat(), v.w.ToFloat()).normalized;
    }
}
