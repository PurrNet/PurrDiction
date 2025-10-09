using System;
using System.Runtime.CompilerServices;
using Real = PurrNet.Prediction.FP64;

namespace Jitter2.LinearMath
{
    using System.Numerics;

// Enables implicit conversion between JVector/JQuaternion and .NET's own numeric types (Vector3 and Quaternion).
// This allows seamless interoperability with .NET libraries that use these types.

    public partial struct JVector
    {
        public static implicit operator JVector((Real x, Real y, Real z) tuple) => new JVector(tuple.x, tuple.y, tuple.z);

        public static JVector Create(Real X, Real Y, Real Z, Real W)
        {
            return new JVector(X, Y, Z);
        }

        public static JVector Create(Real v)
        {
            return new JVector(v, v, v);
        }
    }

    public partial struct JQuaternion
    {
        public static implicit operator JQuaternion((Real x, Real y, Real z, Real w) tuple) => new JQuaternion(tuple.x, tuple.y, tuple.z, tuple.w);
    }

#if DEBUG

    public static class UnsafeBase64Serializer<T> where T : unmanaged
    {
        public static string Serialize(in T value)
        {
            int size = Unsafe.SizeOf<T>();
            byte[] buffer = new byte[size];

            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    *(T*)ptr = value;
                }
            }

            return Convert.ToBase64String(buffer);
        }

        public static T Deserialize(string base64)
        {
            byte[] buffer = Convert.FromBase64String(base64);
            if (buffer.Length != Unsafe.SizeOf<T>())
                throw new InvalidOperationException("Data size does not match type size.");

            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    return *(T*)ptr;
                }
            }
        }
    }
#endif
}
