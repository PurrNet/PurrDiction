using FixMath.NET;
using UnityEngine;

namespace ConversionHelper
{
    public static class MathConverter
    {
        //Vector2
        public static Vector2 Convert(BEPUutilities.Vector2 bepuVector)
        {
            Vector2 toReturn;
            toReturn.x = (float)bepuVector.X;
            toReturn.y = (float)bepuVector.Y;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Vector2 bepuVector, out Vector2 unityVector)
        {
            unityVector.x = (float)bepuVector.X;
            unityVector.y = (float)bepuVector.Y;
        }

        public static BEPUutilities.Vector2 Convert(Vector2 unityVector)
        {
            BEPUutilities.Vector2 toReturn;
            toReturn.X = (Fix64)unityVector.x;
            toReturn.Y = (Fix64)unityVector.y;
            return toReturn;
        }

        public static void Convert(ref Vector2 unityVector, out BEPUutilities.Vector2 bepuVector)
        {
            bepuVector.X = (Fix64)unityVector.x;
            bepuVector.Y = (Fix64)unityVector.y;
        }

        //Vector3
        public static Vector3 Convert(BEPUutilities.Vector3 bepuVector)
        {
            Vector3 toReturn;
            toReturn.x = (float)bepuVector.X;
            toReturn.y = (float)bepuVector.Y;
            toReturn.z = (float)bepuVector.Z;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Vector3 bepuVector, out Vector3 unityVector)
        {
            unityVector.x = (float)bepuVector.X;
            unityVector.y = (float)bepuVector.Y;
            unityVector.z = (float)bepuVector.Z;
        }

        public static BEPUutilities.Vector3 Convert(Vector3 unityVector)
        {
            BEPUutilities.Vector3 toReturn;
            toReturn.X = (Fix64)unityVector.x;
            toReturn.Y = (Fix64)unityVector.y;
            toReturn.Z = (Fix64)unityVector.z;
            return toReturn;
        }

        public static void Convert(ref Vector3 unityVector, out BEPUutilities.Vector3 bepuVector)
        {
            bepuVector.X = (Fix64)unityVector.x;
            bepuVector.Y = (Fix64)unityVector.y;
            bepuVector.Z = (Fix64)unityVector.z;
        }

        public static Vector3[] Convert(BEPUutilities.Vector3[] bepuVectors)
        {
            Vector3[] unityVectors = new Vector3[bepuVectors.Length];
            for (int i = 0; i < bepuVectors.Length; i++)
            {
                Convert(ref bepuVectors[i], out unityVectors[i]);
            }
            return unityVectors;

        }

        public static BEPUutilities.Vector3[] Convert(Vector3[] unityVectors)
        {
            var bepuVectors = new BEPUutilities.Vector3[unityVectors.Length];
            for (int i = 0; i < unityVectors.Length; i++)
            {
                Convert(ref unityVectors[i], out bepuVectors[i]);
            }
            return bepuVectors;

        }

        //Matrix
        public static Matrix4x4 Convert(BEPUutilities.Matrix matrix)
        {
            Convert(ref matrix, out var toReturn);
            return toReturn;
        }

        public static BEPUutilities.Matrix Convert(Matrix4x4 matrix)
        {
            Convert(ref matrix, out BEPUutilities.Matrix toReturn);
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Matrix matrix, out Matrix4x4 unityMatrix)
        {
            unityMatrix.m00 = (float)matrix.M11;
            unityMatrix.m01 = (float)matrix.M12;
            unityMatrix.m02 = (float)matrix.M13;
            unityMatrix.m03 = (float)matrix.M14;
            
            unityMatrix.m10 = (float)matrix.M21;
            unityMatrix.m11 = (float)matrix.M22;
            unityMatrix.m12 = (float)matrix.M23;
            unityMatrix.m13 = (float)matrix.M24;
            
            unityMatrix.m20 = (float)matrix.M31;
            unityMatrix.m21 = (float)matrix.M32;
            unityMatrix.m22 = (float)matrix.M33;
            unityMatrix.m23 = (float)matrix.M34;
            
            unityMatrix.m30 = (float)matrix.M41;
            unityMatrix.m31 = (float)matrix.M42;
            unityMatrix.m32 = (float)matrix.M43;
            unityMatrix.m33 = (float)matrix.M44;

        }

        public static void Convert(ref Matrix4x4 matrix, out BEPUutilities.Matrix bepuMatrix)
        {
            bepuMatrix.M11 = (Fix64)matrix.m00;
            bepuMatrix.M12 = (Fix64)matrix.m01;
            bepuMatrix.M13 = (Fix64)matrix.m02;
            bepuMatrix.M14 = (Fix64)matrix.m03;
            
            bepuMatrix.M21 = (Fix64)matrix.m10;
            bepuMatrix.M22 = (Fix64)matrix.m11;
            bepuMatrix.M23 = (Fix64)matrix.m12;
            bepuMatrix.M24 = (Fix64)matrix.m13;
            
            bepuMatrix.M31 = (Fix64)matrix.m20;
            bepuMatrix.M32 = (Fix64)matrix.m21;
            bepuMatrix.M33 = (Fix64)matrix.m22;
            bepuMatrix.M34 = (Fix64)matrix.m23;
            
            bepuMatrix.M41 = (Fix64)matrix.m30;
            bepuMatrix.M42 = (Fix64)matrix.m31;
            bepuMatrix.M43 = (Fix64)matrix.m32;
            bepuMatrix.M44 = (Fix64)matrix.m33;
        }

        public static Matrix4x4 Convert(BEPUutilities.Matrix3x3 matrix)
        {
            Convert(ref matrix, out var toReturn);
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Matrix3x3 matrix, out Matrix4x4 unityMatrix)
        {
            unityMatrix.m00 = (float)matrix.M11;
            unityMatrix.m01 = (float)matrix.M12;
            unityMatrix.m02 = (float)matrix.M13;
            unityMatrix.m03 = 0;
            
            unityMatrix.m10 = (float)matrix.M21;
            unityMatrix.m11 = (float)matrix.M22;
            unityMatrix.m12 = (float)matrix.M23;
            unityMatrix.m13 = 0;
            
            unityMatrix.m20 = (float)matrix.M31;
            unityMatrix.m21 = (float)matrix.M32;
            unityMatrix.m22 = (float)matrix.M33;
            unityMatrix.m23 = 0;
            
            unityMatrix.m30 = 0;
            unityMatrix.m31 = 0;
            unityMatrix.m32 = 0;
            unityMatrix.m33 = 1;
        }

        public static void Convert(ref Matrix4x4 matrix, out BEPUutilities.Matrix3x3 bepuMatrix)
        {
            bepuMatrix.M11 = (Fix64)matrix.m00;
            bepuMatrix.M12 = (Fix64)matrix.m01;
            bepuMatrix.M13 = (Fix64)matrix.m02;
            
            bepuMatrix.M21 = (Fix64)matrix.m10;
            bepuMatrix.M22 = (Fix64)matrix.m11;
            bepuMatrix.M23 = (Fix64)matrix.m12;
            
            bepuMatrix.M31 = (Fix64)matrix.m20;
            bepuMatrix.M32 = (Fix64)matrix.m21;
            bepuMatrix.M33 = (Fix64)matrix.m22;

        }

        //Quaternion
        public static Quaternion Convert(BEPUutilities.Quaternion quaternion)
        {
            Quaternion toReturn;
            toReturn.x = (float)quaternion.X;
            toReturn.y = (float)quaternion.Y;
            toReturn.z = (float)quaternion.Z;
            toReturn.w = (float)quaternion.W;
            return toReturn;
        }

        public static BEPUutilities.Quaternion Convert(Quaternion quaternion)
        {
            BEPUutilities.Quaternion toReturn;
            toReturn.X = (Fix64)quaternion.x;
            toReturn.Y = (Fix64)quaternion.y;
            toReturn.Z = (Fix64)quaternion.z;
            toReturn.W = (Fix64)quaternion.w;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Quaternion bepuQuaternion, out Quaternion quaternion)
        {
            quaternion.x = (float)bepuQuaternion.X;
            quaternion.y = (float)bepuQuaternion.Y;
            quaternion.z = (float)bepuQuaternion.Z;
            quaternion.w = (float)bepuQuaternion.W;
        }

        public static void Convert(ref Quaternion quaternion, out  BEPUutilities.Quaternion bepuQuaternion)
        {
            bepuQuaternion.X = (Fix64)quaternion.x;
            bepuQuaternion.Y = (Fix64)quaternion.y;
            bepuQuaternion.Z = (Fix64)quaternion.z;
            bepuQuaternion.W = (Fix64)quaternion.w;
        }

        //Ray
        public static BEPUutilities.Ray Convert(Ray ray)
        {
            BEPUutilities.Ray toReturn;
            
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out toReturn.Position);
            Convert(ref direction, out toReturn.Direction);
            
            return toReturn;
        }

        public static void Convert(ref Ray ray, out BEPUutilities.Ray bepuRay)
        {
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out bepuRay.Position);
            Convert(ref direction, out bepuRay.Direction);
        }

        public static Ray Convert(BEPUutilities.Ray ray)
        {
            Convert(ref ray.Position, out var position);
            Convert(ref ray.Direction, out var direction);
            return new Ray(position, direction);
        }

        public static void Convert(ref BEPUutilities.Ray ray, out Ray unityRay)
        {
            Convert(ref ray.Position, out var pos);
            Convert(ref ray.Direction, out var dir);
            
            unityRay = new Ray(pos, dir);
        }

        //BoundingBox
        public static Bounds Convert(BEPUutilities.BoundingBox boundingBox)
        {
            Bounds toReturn = new();
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            toReturn.SetMinMax(min, max);
            return toReturn;
        }

        public static BEPUutilities.BoundingBox Convert(Bounds boundingBox)
        {
            BEPUutilities.BoundingBox toReturn;
            var min = boundingBox.min;
            var max = boundingBox.max;
            Convert(ref min, out toReturn.Min);
            Convert(ref max, out toReturn.Max);
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.BoundingBox boundingBox, out Bounds unityBoundingBox)
        {
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            
            unityBoundingBox = new Bounds();
            unityBoundingBox.SetMinMax(min, max);
        }

        public static void Convert(ref Bounds boundingBox, out BEPUutilities.BoundingBox bepuBoundingBox)
        {
            var min = boundingBox.min;
            var max = boundingBox.max;
            
            Convert(ref min, out bepuBoundingBox.Min);
            Convert(ref max, out bepuBoundingBox.Max);
        }

        //Plane
        public static Plane Convert(BEPUutilities.Plane plane)
        {
            Convert(ref plane.Normal, out var normal);
            return new Plane(normal, (float)plane.D);
        }

        public static BEPUutilities.Plane Convert(Plane plane)
        {
            BEPUutilities.Plane toReturn;
            var normal = plane.normal;
            Convert(ref normal, out toReturn.Normal);
            toReturn.D = (Fix64)plane.distance;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.Plane plane, out Plane unityPlane)
        {
            Convert(ref plane.Normal, out var normal);
            unityPlane = new Plane(normal, (float)plane.D);
        }

        public static void Convert(ref Plane plane, out BEPUutilities.Plane bepuPlane)
        {
            var normal = plane.normal;
            Convert(ref normal, out bepuPlane.Normal);
            bepuPlane.D = (Fix64)plane.distance;
        }
    }
}
