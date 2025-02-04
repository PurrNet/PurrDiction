



using FixMath.NET;

namespace BEPUutilities
{
    /// <summary>
    /// 2 row, 3 column matrix.
    /// </summary>
    public struct Matrix2x3
    {
        /// <summary>
        /// Value at row 1, column 1 of the matrix.
        /// </summary>
        public FP M11;

        /// <summary>
        /// Value at row 1, column 2 of the matrix.
        /// </summary>
        public FP M12;

        /// <summary>
        /// Value at row 1, column 2 of the matrix.
        /// </summary>
        public FP M13;

        /// <summary>
        /// Value at row 2, column 1 of the matrix.
        /// </summary>
        public FP M21;

        /// <summary>
        /// Value at row 2, column 2 of the matrix.
        /// </summary>
        public FP M22;

        /// <summary>
        /// Value at row 2, column 3 of the matrix.
        /// </summary>
        public FP M23;


        /// <summary>
        /// Constructs a new 2 row, 2 column matrix.
        /// </summary>
        /// <param name="m11">Value at row 1, column 1 of the matrix.</param>
        /// <param name="m12">Value at row 1, column 2 of the matrix.</param>
        /// <param name="m13">Value at row 1, column 3 of the matrix.</param>
        /// <param name="m21">Value at row 2, column 1 of the matrix.</param>
        /// <param name="m22">Value at row 2, column 2 of the matrix.</param>
        /// <param name="m23">Value at row 2, column 3 of the matrix.</param>
        public Matrix2x3(FP m11, FP m12, FP m13, FP m21, FP m22, FP m23)
        {
            M11 = m11;
            M12 = m12;
            M13 = m13;
            M21 = m21;
            M22 = m22;
            M23 = m23;
        }

        /// <summary>
        /// Adds the two matrices together on a per-element basis.
        /// </summary>
        /// <param name="a">First matrix to add.</param>
        /// <param name="b">Second matrix to add.</param>
        /// <param name="result">Sum of the two matrices.</param>
        public static void Add(ref Matrix2x3 a, ref Matrix2x3 b, out Matrix2x3 result)
        {
            FP m11 = a.M11 + b.M11;
            FP m12 = a.M12 + b.M12;
            FP m13 = a.M13 + b.M13;

            FP m21 = a.M21 + b.M21;
            FP m22 = a.M22 + b.M22;
            FP m23 = a.M23 + b.M23;

            result.M11 = m11;
            result.M12 = m12;
            result.M13 = m13;

            result.M21 = m21;
            result.M22 = m22;
            result.M23 = m23;
        }


        /// <summary>
        /// Multiplies the two matrices.
        /// </summary>
        /// <param name="a">First matrix to multiply.</param>
        /// <param name="b">Second matrix to multiply.</param>
        /// <param name="result">Product of the multiplication.</param>
        public static void Multiply(ref Matrix2x3 a, ref Matrix3x3 b, out Matrix2x3 result)
        {
            FP resultM11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31;
            FP resultM12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32;
            FP resultM13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33;

            FP resultM21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31;
            FP resultM22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32;
            FP resultM23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33;

            result.M11 = resultM11;
            result.M12 = resultM12;
            result.M13 = resultM13;

            result.M21 = resultM21;
            result.M22 = resultM22;
            result.M23 = resultM23;
        }

        /// <summary>
        /// Multiplies the two matrices.
        /// </summary>
        /// <param name="a">First matrix to multiply.</param>
        /// <param name="b">Second matrix to multiply.</param>
        /// <param name="result">Product of the multiplication.</param>
        public static void Multiply(ref Matrix2x3 a, ref Matrix b, out Matrix2x3 result)
        {
            FP resultM11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31;
            FP resultM12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32;
            FP resultM13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33;

            FP resultM21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31;
            FP resultM22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32;
            FP resultM23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33;

            result.M11 = resultM11;
            result.M12 = resultM12;
            result.M13 = resultM13;

            result.M21 = resultM21;
            result.M22 = resultM22;
            result.M23 = resultM23;
        }

        /// <summary>
        /// Negates every element in the matrix.
        /// </summary>
        /// <param name="matrix">Matrix to negate.</param>
        /// <param name="result">Negated matrix.</param>
        public static void Negate(ref Matrix2x3 matrix, out Matrix2x3 result)
        {
            FP m11 = -matrix.M11;
            FP m12 = -matrix.M12;
            FP m13 = -matrix.M13;

            FP m21 = -matrix.M21;
            FP m22 = -matrix.M22;
            FP m23 = -matrix.M23;

            result.M11 = m11;
            result.M12 = m12;
            result.M13 = m13;

            result.M21 = m21;
            result.M22 = m22;
            result.M23 = m23;
        }

        /// <summary>
        /// Subtracts the two matrices from each other on a per-element basis.
        /// </summary>
        /// <param name="a">First matrix to subtract.</param>
        /// <param name="b">Second matrix to subtract.</param>
        /// <param name="result">Difference of the two matrices.</param>
        public static void Subtract(ref Matrix2x3 a, ref Matrix2x3 b, out Matrix2x3 result)
        {
            FP m11 = a.M11 - b.M11;
            FP m12 = a.M12 - b.M12;
            FP m13 = a.M13 - b.M13;

            FP m21 = a.M21 - b.M21;
            FP m22 = a.M22 - b.M22;
            FP m23 = a.M23 - b.M23;

            result.M11 = m11;
            result.M12 = m12;
            result.M13 = m13;

            result.M21 = m21;
            result.M22 = m22;
            result.M23 = m23;
        }


        /// <summary>
        /// Transforms the vector by the matrix.
        /// </summary>
        /// <param name="v">Vector2 to transform.  Considered to be a row vector for purposes of multiplication.</param>
        /// <param name="matrix">Matrix to use as the transformation.</param>
        /// <param name="result">Row vector product of the transformation.</param>
        public static void Transform(ref FPVector2 v, ref Matrix2x3 matrix, out FPVector3 result)
        {
#if !WINDOWS
            result = new FPVector3();
#endif
            result.x = v.x * matrix.M11 + v.y * matrix.M21;
            result.y = v.x * matrix.M12 + v.y * matrix.M22;
            result.z = v.x * matrix.M13 + v.y * matrix.M23;
        }

        /// <summary>
        /// Transforms the vector by the matrix.
        /// </summary>
        /// <param name="v">Vector2 to transform.  Considered to be a column vector for purposes of multiplication.</param>
        /// <param name="matrix">Matrix to use as the transformation.</param>
        /// <param name="result">Column vector product of the transformation.</param>
        public static void Transform(ref FPVector3 v, ref Matrix2x3 matrix, out FPVector2 result)
        {
#if !WINDOWS
            result = new FPVector2();
#endif
            result.x = matrix.M11 * v.x + matrix.M12 * v.y + matrix.M13 * v.z;
            result.y = matrix.M21 * v.x + matrix.M22 * v.y + matrix.M23 * v.z;
        }


        /// <summary>
        /// Computes the transposed matrix of a matrix.
        /// </summary>
        /// <param name="matrix">Matrix to transpose.</param>
        /// <param name="result">Transposed matrix.</param>
        public static void Transpose(ref Matrix2x3 matrix, out Matrix3x2 result)
        {
            result.M11 = matrix.M11;
            result.M12 = matrix.M21;

            result.M21 = matrix.M12;
            result.M22 = matrix.M22;

            result.M31 = matrix.M13;
            result.M32 = matrix.M23;
        }


        /// <summary>
        /// Creates a string representation of the matrix.
        /// </summary>
        /// <returns>A string representation of the matrix.</returns>
        public override string ToString()
        {
            return "{" + M11 + ", " + M12 + ", " + M13 + "} " +
                   "{" + M21 + ", " + M22 + ", " + M23 + "}";
        }
    }
}