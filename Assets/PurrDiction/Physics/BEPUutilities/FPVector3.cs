using FixMath.NET;
using System;
using UnityEngine.Serialization;

namespace BEPUutilities
{
    /// <summary>
    /// Provides XNA-like 3D vector math.
    /// </summary>
    [Serializable]
    public struct FPVector3 : IEquatable<FPVector3>
    {
        /// <summary>
        /// X component of the vector.
        /// </summary>
        public FP x;
        /// <summary>
        /// Y component of the vector.
        /// </summary>
        public FP y;
        /// <summary>
        /// Z component of the vector.
        /// </summary>
        public FP z;

        /// <summary>
        /// Returns the magnitude of the vector.
        /// </summary>
        public FP magnitude => FP.Sqrt(x * x + y * y + z * z);

        /// <summary>
        /// Returns the normalized version of the vector.
        /// </summary>
        public FPVector3 normalized => this / magnitude;

        /// <summary>
        /// Constructs a new 3d vector.
        /// </summary>
        /// <param name="x">X component of the vector.</param>
        /// <param name="y">Y component of the vector.</param>
        /// <param name="z">Z component of the vector.</param>
        public FPVector3(FP x, FP y, FP z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        /// <summary>
        /// Constructs a new 3d vector.
        /// </summary>
        /// <param name="xy">X and Y components of the vector.</param>
        /// <param name="z">Z component of the vector.</param>
        public FPVector3(FPVector2 xy, FP z)
        {
            this.x = xy.x;
            this.y = xy.y;
            this.z = z;
        }

        /// <summary>
        /// Constructs a new 3d vector.
        /// </summary>
        /// <param name="x">X component of the vector.</param>
        /// <param name="yz">Y and Z components of the vector.</param>
        public FPVector3(FP x, FPVector2 yz)
        {
            this.x = x;
            this.y = yz.x;
            this.z = yz.y;
        }

        /// <summary>
        /// Computes the squared length of the vector.
        /// </summary>
        /// <returns>Squared length of the vector.</returns>
        public FP LengthSquared()
        {
            return x * x + y * y + z * z;
        }

        /// <summary>
        /// Computes the length of the vector.
        /// </summary>
        /// <returns>Length of the vector.</returns>
        public FP Length()
        {
            return FP.Sqrt(x * x + y * y + z * z);
        }

        /// <summary>
        /// Normalizes the vector.
        /// </summary>
        public void Normalize()
        {
            FP inverse = F64.C1 / FP.Sqrt(x * x + y * y + z * z);
            x *= inverse;
            y *= inverse;
            z *= inverse;
        }

        /// <summary>
        /// Gets a string representation of the vector.
        /// </summary>
        /// <returns>String representing the vector.</returns>
        public override string ToString()
        {
            return "{" + x + ", " + y + ", " + z + "}";
        }

        /// <summary>
        /// Computes the dot product of two vectors.
        /// </summary>
        /// <param name="a">First vector in the product.</param>
        /// <param name="b">Second vector in the product.</param>
        /// <returns>Resulting dot product.</returns>
        public static FP Dot(FPVector3 a, FPVector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        /// <summary>
        /// Computes the dot product of two vectors.
        /// </summary>
        /// <param name="a">First vector in the product.</param>
        /// <param name="b">Second vector in the product.</param>
        /// <param name="product">Resulting dot product.</param>
        public static void Dot(ref FPVector3 a, ref FPVector3 b, out FP product)
        {
            product = a.x * b.x + a.y * b.y + a.z * b.z;
        }
        /// <summary>
        /// Adds two vectors together.
        /// </summary>
        /// <param name="a">First vector to add.</param>
        /// <param name="b">Second vector to add.</param>
        /// <param name="sum">Sum of the two vectors.</param>
        public static void Add(ref FPVector3 a, ref FPVector3 b, out FPVector3 sum)
        {
            sum.x = a.x + b.x;
            sum.y = a.y + b.y;
            sum.z = a.z + b.z;
        }
        /// <summary>
        /// Subtracts two vectors.
        /// </summary>
        /// <param name="a">Vector to subtract from.</param>
        /// <param name="b">Vector to subtract from the first vector.</param>
        /// <param name="difference">Result of the subtraction.</param>
        public static void Subtract(ref FPVector3 a, ref FPVector3 b, out FPVector3 difference)
        {
            difference.x = a.x - b.x;
            difference.y = a.y - b.y;
            difference.z = a.z - b.z;
        }
        /// <summary>
        /// Scales a vector.
        /// </summary>
        /// <param name="v">Vector to scale.</param>
        /// <param name="scale">Amount to scale.</param>
        /// <param name="result">Scaled vector.</param>
        public static void Multiply(ref FPVector3 v, FP scale, out FPVector3 result)
        {
            result.x = v.x * scale;
            result.y = v.y * scale;
            result.z = v.z * scale;
        }

        /// <summary>
        /// Multiplies two vectors on a per-component basis.
        /// </summary>
        /// <param name="a">First vector to multiply.</param>
        /// <param name="b">Second vector to multiply.</param>
        /// <param name="result">Result of the componentwise multiplication.</param>
        public static void Multiply(ref FPVector3 a, ref FPVector3 b, out FPVector3 result)
        {
            result.x = a.x * b.x;
            result.y = a.y * b.y;
            result.z = a.z * b.z;
        }

        /// <summary>
        /// Divides a vector's components by some amount.
        /// </summary>
        /// <param name="v">Vector to divide.</param>
        /// <param name="divisor">Value to divide the vector's components.</param>
        /// <param name="result">Result of the division.</param>
        public static void Divide(ref FPVector3 v, FP divisor, out FPVector3 result)
        {
            FP inverse = F64.C1 / divisor;
            result.x = v.x * inverse;
            result.y = v.y * inverse;
            result.z = v.z * inverse;
        }
        /// <summary>
        /// Scales a vector.
        /// </summary>
        /// <param name="v">Vector to scale.</param>
        /// <param name="f">Amount to scale.</param>
        /// <returns>Scaled vector.</returns>
        public static FPVector3 operator *(FPVector3 v, FP f)
        {
            FPVector3 toReturn;
            toReturn.x = v.x * f;
            toReturn.y = v.y * f;
            toReturn.z = v.z * f;
            return toReturn;
        }

        /// <summary>
        /// Scales a vector.
        /// </summary>
        /// <param name="v">Vector to scale.</param>
        /// <param name="f">Amount to scale.</param>
        /// <returns>Scaled vector.</returns>
        public static FPVector3 operator *(FP f, FPVector3 v)
        {
            FPVector3 toReturn;
            toReturn.x = v.x * f;
            toReturn.y = v.y * f;
            toReturn.z = v.z * f;
            return toReturn;
        }

        /// <summary>
        /// Multiplies two vectors on a per-component basis.
        /// </summary>
        /// <param name="a">First vector to multiply.</param>
        /// <param name="b">Second vector to multiply.</param>
        /// <returns>Result of the componentwise multiplication.</returns>
        public static FPVector3 operator *(FPVector3 a, FPVector3 b)
        {
            FPVector3 result;
            Multiply(ref a, ref b, out result);
            return result;
        }

        /// <summary>
        /// Divides a vector's components by some amount.
        /// </summary>
        /// <param name="v">Vector to divide.</param>
        /// <param name="f">Value to divide the vector's components.</param>
        /// <returns>Result of the division.</returns>
        public static FPVector3 operator /(FPVector3 v, FP f)
        {
            FPVector3 toReturn;
            f = F64.C1 / f;
            toReturn.x = v.x * f;
            toReturn.y = v.y * f;
            toReturn.z = v.z * f;
            return toReturn;
        }
        /// <summary>
        /// Subtracts two vectors.
        /// </summary>
        /// <param name="a">Vector to subtract from.</param>
        /// <param name="b">Vector to subtract from the first vector.</param>
        /// <returns>Result of the subtraction.</returns>
        public static FPVector3 operator -(FPVector3 a, FPVector3 b)
        {
            FPVector3 v;
            v.x = a.x - b.x;
            v.y = a.y - b.y;
            v.z = a.z - b.z;
            return v;
        }
        /// <summary>
        /// Adds two vectors together.
        /// </summary>
        /// <param name="a">First vector to add.</param>
        /// <param name="b">Second vector to add.</param>
        /// <returns>Sum of the two vectors.</returns>
        public static FPVector3 operator +(FPVector3 a, FPVector3 b)
        {
            FPVector3 v;
            v.x = a.x + b.x;
            v.y = a.y + b.y;
            v.z = a.z + b.z;
            return v;
        }


        /// <summary>
        /// Negates the vector.
        /// </summary>
        /// <param name="v">Vector to negate.</param>
        /// <returns>Negated vector.</returns>
        public static FPVector3 operator -(FPVector3 v)
        {
            v.x = -v.x;
            v.y = -v.y;
            v.z = -v.z;
            return v;
        }
        /// <summary>
        /// Tests two vectors for componentwise equivalence.
        /// </summary>
        /// <param name="a">First vector to test for equivalence.</param>
        /// <param name="b">Second vector to test for equivalence.</param>
        /// <returns>Whether the vectors were equivalent.</returns>
        public static bool operator ==(FPVector3 a, FPVector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }
        /// <summary>
        /// Tests two vectors for componentwise inequivalence.
        /// </summary>
        /// <param name="a">First vector to test for inequivalence.</param>
        /// <param name="b">Second vector to test for inequivalence.</param>
        /// <returns>Whether the vectors were inequivalent.</returns>
        public static bool operator !=(FPVector3 a, FPVector3 b)
        {
            return a.x != b.x || a.y != b.y || a.z != b.z;
        }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <returns>
        /// true if the current object is equal to the <paramref name="other"/> parameter; otherwise, false.
        /// </returns>
        /// <param name="other">An object to compare with this object.</param>
        public bool Equals(FPVector3 other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        /// <summary>
        /// Indicates whether this instance and a specified object are equal.
        /// </summary>
        /// <returns>
        /// true if <paramref name="obj"/> and this instance are the same type and represent the same value; otherwise, false.
        /// </returns>
        /// <param name="obj">Another object to compare to. </param><filterpriority>2</filterpriority>
        public override bool Equals(object obj)
        {
            if (obj is FPVector3)
            {
                return Equals((FPVector3)obj);
            }
            return false;
        }

        /// <summary>
        /// Returns the hash code for this instance.
        /// </summary>
        /// <returns>
        /// A 32-bit signed integer that is the hash code for this instance.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public override int GetHashCode()
        {
            return x.GetHashCode() + y.GetHashCode() + z.GetHashCode();
        }


        /// <summary>
        /// Computes the squared distance between two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <param name="distanceSquared">Squared distance between the two vectors.</param>
        public static void DistanceSquared(ref FPVector3 a, ref FPVector3 b, out FP distanceSquared)
        {
            FP x = a.x - b.x;
            FP y = a.y - b.y;
            FP z = a.z - b.z;
            distanceSquared = x * x + y * y + z * z;
        }

        /// <summary>
        /// Computes the squared distance between two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Squared distance between the two vectors.</returns>
        public static FP DistanceSquared(FPVector3 a, FPVector3 b)
        {
            FP x = a.x - b.x;
            FP y = a.y - b.y;
            FP z = a.z - b.z;
            return x * x + y * y + z * z;
        }


        /// <summary>
        /// Computes the distance between two two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <param name="distance">Distance between the two vectors.</param>
        public static void Distance(ref FPVector3 a, ref FPVector3 b, out FP distance)
        {
            FP x = a.x - b.x;
            FP y = a.y - b.y;
            FP z = a.z - b.z;
            distance = FP.Sqrt(x * x + y * y + z * z);
        }
        /// <summary>
        /// Computes the distance between two two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Distance between the two vectors.</returns>
        public static FP Distance(FPVector3 a, FPVector3 b)
        {
            FP toReturn;
            Distance(ref a, ref b, out toReturn);
            return toReturn;
        }

        /// <summary>
        /// Gets the zero vector.
        /// </summary>
        public static FPVector3 zero
        {
            get
            {
                return new FPVector3();
            }
        }

        /// <summary>
        /// Gets the up vector (0,1,0).
        /// </summary>
        public static FPVector3 up
        {
            get
            {
                return new FPVector3()
                {
                    x = F64.C0,
                    y = F64.C1,
                    z = F64.C0
				};
            }
        }

        /// <summary>
        /// Gets the down vector (0,-1,0).
        /// </summary>
        public static FPVector3 down
        {
            get
            {
                return new FPVector3()
                {
                    x = F64.C0,
                    y = -1,
                    z = F64.C0
				};
            }
        }

        /// <summary>
        /// Gets the right vector (1,0,0).
        /// </summary>
        public static FPVector3 right
        {
            get
            {
                return new FPVector3()
                {
                    x = F64.C1,
                    y = F64.C0,
                    z = F64.C0
				};
            }
        }

        /// <summary>
        /// Gets the left vector (-1,0,0).
        /// </summary>
        public static FPVector3 left
        {
            get
            {
                return new FPVector3()
                {
                    x = -1,
                    y = F64.C0,
                    z = F64.C0
				};
            }
        }

        /// <summary>
        /// Gets the forward vector (0,0,-1).
        /// </summary>
        public static FPVector3 forward
        {
            get
            {
                return new FPVector3()
                {
                    x = F64.C0,
                    y = F64.C0,
                    z = -1
                };
            }
        }

        /// <summary>
        /// Gets the back vector (0,0,1).
        /// </summary>
        public static FPVector3 backward
        {
            get
            {
                return new FPVector3()
                {
                    x = F64.C0,
                    y = F64.C0,
                    z = F64.C1
				};
            }
        }

        /// <summary>
        /// Gets a vector pointing along the X axis.
        /// </summary>
        public static FPVector3 unitX
        {
            get { return new FPVector3 { x = F64.C1 }; }
        }

        /// <summary>
        /// Gets a vector pointing along the Y axis.
        /// </summary>
        public static FPVector3 unitY
        {
            get { return new FPVector3 { y = F64.C1 }; }
        }

        /// <summary>
        /// Gets a vector pointing along the Z axis.
        /// </summary>
        public static FPVector3 unitZ
        {
            get { return new FPVector3 { z = F64.C1 }; }
        }

        /// <summary>
        /// Computes the cross product between two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Cross product of the two vectors.</returns>
        public static FPVector3 Cross(FPVector3 a, FPVector3 b)
        {
            FPVector3 toReturn;
            FPVector3.Cross(ref a, ref b, out toReturn);
            return toReturn;
        }
        /// <summary>
        /// Computes the cross product between two vectors.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <param name="result">Cross product of the two vectors.</param>
        public static void Cross(ref FPVector3 a, ref FPVector3 b, out FPVector3 result)
        {
            FP resultX = a.y * b.z - a.z * b.y;
            FP resultY = a.z * b.x - a.x * b.z;
            FP resultZ = a.x * b.y - a.y * b.x;
            result.x = resultX;
            result.y = resultY;
            result.z = resultZ;
        }

        /// <summary>
        /// Normalizes the given vector.
        /// </summary>
        /// <param name="v">Vector to normalize.</param>
        /// <returns>Normalized vector.</returns>
        public static FPVector3 Normalize(FPVector3 v)
        {
            FPVector3 toReturn;
            FPVector3.Normalize(ref v, out toReturn);
            return toReturn;
        }

        /// <summary>
        /// Normalizes the given vector.
        /// </summary>
        /// <param name="v">Vector to normalize.</param>
        /// <param name="result">Normalized vector.</param>
        public static void Normalize(ref FPVector3 v, out FPVector3 result)
        {
            FP inverse = F64.C1 / FP.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            result.x = v.x * inverse;
            result.y = v.y * inverse;
            result.z = v.z * inverse;
        }

        /// <summary>
        /// Negates a vector.
        /// </summary>
        /// <param name="v">Vector to negate.</param>
        /// <param name="negated">Negated vector.</param>
        public static void Negate(ref FPVector3 v, out FPVector3 negated)
        {
            negated.x = -v.x;
            negated.y = -v.y;
            negated.z = -v.z;
        }

        /// <summary>
        /// Computes the absolute value of the input vector.
        /// </summary>
        /// <param name="v">Vector to take the absolute value of.</param>
        /// <param name="result">Vector with nonnegative elements.</param>
        public static void Abs(ref FPVector3 v, out FPVector3 result)
        {
            if (v.x < F64.C0)
                result.x = -v.x;
            else
                result.x = v.x;
            if (v.y < F64.C0)
                result.y = -v.y;
            else
                result.y = v.y;
            if (v.z < F64.C0)
                result.z = -v.z;
            else
                result.z = v.z;
        }

        /// <summary>
        /// Computes the absolute value of the input vector.
        /// </summary>
        /// <param name="v">Vector to take the absolute value of.</param>
        /// <returns>Vector with nonnegative elements.</returns>
        public static FPVector3 Abs(FPVector3 v)
        {
            FPVector3 result;
            Abs(ref v, out result);
            return result;
        }

        /// <summary>
        /// Creates a vector from the lesser values in each vector.
        /// </summary>
        /// <param name="a">First input vector to compare values from.</param>
        /// <param name="b">Second input vector to compare values from.</param>
        /// <param name="min">Vector containing the lesser values of each vector.</param>
        public static void Min(ref FPVector3 a, ref FPVector3 b, out FPVector3 min)
        {
            min.x = a.x < b.x ? a.x : b.x;
            min.y = a.y < b.y ? a.y : b.y;
            min.z = a.z < b.z ? a.z : b.z;
        }

        /// <summary>
        /// Creates a vector from the lesser values in each vector.
        /// </summary>
        /// <param name="a">First input vector to compare values from.</param>
        /// <param name="b">Second input vector to compare values from.</param>
        /// <returns>Vector containing the lesser values of each vector.</returns>
        public static FPVector3 Min(FPVector3 a, FPVector3 b)
        {
            FPVector3 result;
            Min(ref a, ref b, out result);
            return result;
        }


        /// <summary>
        /// Creates a vector from the greater values in each vector.
        /// </summary>
        /// <param name="a">First input vector to compare values from.</param>
        /// <param name="b">Second input vector to compare values from.</param>
        /// <param name="max">Vector containing the greater values of each vector.</param>
        public static void Max(ref FPVector3 a, ref FPVector3 b, out FPVector3 max)
        {
            max.x = a.x > b.x ? a.x : b.x;
            max.y = a.y > b.y ? a.y : b.y;
            max.z = a.z > b.z ? a.z : b.z;
        }

        /// <summary>
        /// Creates a vector from the greater values in each vector.
        /// </summary>
        /// <param name="a">First input vector to compare values from.</param>
        /// <param name="b">Second input vector to compare values from.</param>
        /// <returns>Vector containing the greater values of each vector.</returns>
        public static FPVector3 Max(FPVector3 a, FPVector3 b)
        {
            FPVector3 result;
            Max(ref a, ref b, out result);
            return result;
        }

        /// <summary>
        /// Computes an interpolated state between two vectors.
        /// </summary>
        /// <param name="start">Starting location of the interpolation.</param>
        /// <param name="end">Ending location of the interpolation.</param>
        /// <param name="interpolationAmount">Amount of the end location to use.</param>
        /// <returns>Interpolated intermediate state.</returns>
        public static FPVector3 Lerp(FPVector3 start, FPVector3 end, FP interpolationAmount)
        {
            Lerp(ref start, ref end, interpolationAmount, out var toReturn);
            return toReturn;
        }
        /// <summary>
        /// Computes an interpolated state between two vectors.
        /// </summary>
        /// <param name="start">Starting location of the interpolation.</param>
        /// <param name="end">Ending location of the interpolation.</param>
        /// <param name="interpolationAmount">Amount of the end location to use.</param>
        /// <param name="result">Interpolated intermediate state.</param>
        public static void Lerp(ref FPVector3 start, ref FPVector3 end, FP interpolationAmount, out FPVector3 result)
        {
            FP startAmount = F64.C1 - interpolationAmount;
            result.x = start.x * startAmount + end.x * interpolationAmount;
            result.y = start.y * startAmount + end.y * interpolationAmount;
            result.z = start.z * startAmount + end.z * interpolationAmount;
        }

        /// <summary>
        /// Computes an intermediate location using hermite interpolation.
        /// </summary>
        /// <param name="value1">First position.</param>
        /// <param name="tangent1">Tangent associated with the first position.</param>
        /// <param name="value2">Second position.</param>
        /// <param name="tangent2">Tangent associated with the second position.</param>
        /// <param name="interpolationAmount">Amount of the second point to use.</param>
        /// <param name="result">Interpolated intermediate state.</param>
        public static void Hermite(ref FPVector3 value1, ref FPVector3 tangent1, ref FPVector3 value2, ref FPVector3 tangent2, FP interpolationAmount, out FPVector3 result)
        {
            FP weightSquared = interpolationAmount * interpolationAmount;
            FP weightCubed = interpolationAmount * weightSquared;
            FP value1Blend = F64.C2 * weightCubed - F64.C3 * weightSquared + F64.C1;
            FP tangent1Blend = weightCubed - F64.C2 * weightSquared + interpolationAmount;
            FP value2Blend = -2 * weightCubed + F64.C3 * weightSquared;
            FP tangent2Blend = weightCubed - weightSquared;
            result.x = value1.x * value1Blend + value2.x * value2Blend + tangent1.x * tangent1Blend + tangent2.x * tangent2Blend;
            result.y = value1.y * value1Blend + value2.y * value2Blend + tangent1.y * tangent1Blend + tangent2.y * tangent2Blend;
            result.z = value1.z * value1Blend + value2.z * value2Blend + tangent1.z * tangent1Blend + tangent2.z * tangent2Blend;
        }
        /// <summary>
        /// Computes an intermediate location using hermite interpolation.
        /// </summary>
        /// <param name="value1">First position.</param>
        /// <param name="tangent1">Tangent associated with the first position.</param>
        /// <param name="value2">Second position.</param>
        /// <param name="tangent2">Tangent associated with the second position.</param>
        /// <param name="interpolationAmount">Amount of the second point to use.</param>
        /// <returns>Interpolated intermediate state.</returns>
        public static FPVector3 Hermite(FPVector3 value1, FPVector3 tangent1, FPVector3 value2, FPVector3 tangent2, FP interpolationAmount)
        {
            FPVector3 toReturn;
            Hermite(ref value1, ref tangent1, ref value2, ref tangent2, interpolationAmount, out toReturn);
            return toReturn;
        }
    }
}
