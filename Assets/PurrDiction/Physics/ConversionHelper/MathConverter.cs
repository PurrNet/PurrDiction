using BEPUutilities;
using FixMath.NET;
using UnityEngine;

namespace ConversionHelper
{
    public static class MathConverter
    {
        //Vector2
        public static Vector2 Convert(FPVector2 bepuVector)
        {
            Vector2 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            return toReturn;
        }
        
        public static Vector2 ToVector2(this FPVector2 bepuVector)
        {
            Vector2 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            return toReturn;
        }

        public static void Convert(ref FPVector2 bepuVector, out Vector2 unityVector)
        {
            unityVector.x = (float)bepuVector.x;
            unityVector.y = (float)bepuVector.y;
        }

        public static FPVector2 Convert(Vector2 unityVector)
        {
            FPVector2 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            return toReturn;
        }

        public static FPVector2 ToFPVector2(this Vector2 unityVector)
        {
            FPVector2 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            return toReturn;
        }

        public static void Convert(ref Vector2 unityVector, out FPVector2 bepuVector)
        {
            bepuVector.x = (FP)unityVector.x;
            bepuVector.y = (FP)unityVector.y;
        }

        //Vector3
        public static Vector3 Convert(FPVector3 bepuVector)
        {
            Vector3 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            toReturn.z = (float)bepuVector.z;
            return toReturn;
        }
        
        public static Vector3 ToVector3(this FPVector3 bepuVector)
        {
            Vector3 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            toReturn.z = (float)bepuVector.z;
            return toReturn;
        }

        public static void Convert(ref FPVector3 bepuVector, out Vector3 unityVector)
        {
            unityVector.x = (float)bepuVector.x;
            unityVector.y = (float)bepuVector.y;
            unityVector.z = (float)bepuVector.z;
        }

        public static FPVector3 Convert(Vector3 unityVector)
        {
            FPVector3 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            toReturn.z = (FP)unityVector.z;
            return toReturn;
        }
        
        public static FPVector3 ToFPVector3(this Vector3 unityVector)
        {
            FPVector3 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            toReturn.z = (FP)unityVector.z;
            return toReturn;
        }

        public static void Convert(ref Vector3 unityVector, out FPVector3 bepuVector)
        {
            bepuVector.x = (FP)unityVector.x;
            bepuVector.y = (FP)unityVector.y;
            bepuVector.z = (FP)unityVector.z;
        }

        public static Vector3[] Convert(FPVector3[] bepuVectors)
        {
            Vector3[] unityVectors = new Vector3[bepuVectors.Length];
            for (int i = 0; i < bepuVectors.Length; i++)
            {
                Convert(ref bepuVectors[i], out unityVectors[i]);
            }
            return unityVectors;

        }

        public static FPVector3[] Convert(Vector3[] unityVectors)
        {
            var bepuVectors = new FPVector3[unityVectors.Length];
            for (int i = 0; i < unityVectors.Length; i++)
            {
                Convert(ref unityVectors[i], out bepuVectors[i]);
            }
            return bepuVectors;

        }

        //Matrix
        public static Matrix4x4 Convert(Matrix matrix)
        {
            Convert(ref matrix, out var toReturn);
            return toReturn;
        }

        public static Matrix Convert(Matrix4x4 matrix)
        {
            Convert(ref matrix, out Matrix toReturn);
            return toReturn;
        }

        public static void Convert(ref Matrix matrix, out Matrix4x4 unityMatrix)
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

        public static void Convert(ref Matrix4x4 matrix, out Matrix bepuMatrix)
        {
            bepuMatrix.M11 = (FP)matrix.m00;
            bepuMatrix.M12 = (FP)matrix.m01;
            bepuMatrix.M13 = (FP)matrix.m02;
            bepuMatrix.M14 = (FP)matrix.m03;
            
            bepuMatrix.M21 = (FP)matrix.m10;
            bepuMatrix.M22 = (FP)matrix.m11;
            bepuMatrix.M23 = (FP)matrix.m12;
            bepuMatrix.M24 = (FP)matrix.m13;
            
            bepuMatrix.M31 = (FP)matrix.m20;
            bepuMatrix.M32 = (FP)matrix.m21;
            bepuMatrix.M33 = (FP)matrix.m22;
            bepuMatrix.M34 = (FP)matrix.m23;
            
            bepuMatrix.M41 = (FP)matrix.m30;
            bepuMatrix.M42 = (FP)matrix.m31;
            bepuMatrix.M43 = (FP)matrix.m32;
            bepuMatrix.M44 = (FP)matrix.m33;
        }

        public static Matrix4x4 Convert(Matrix3x3 matrix)
        {
            Convert(ref matrix, out var toReturn);
            return toReturn;
        }

        public static void Convert(ref Matrix3x3 matrix, out Matrix4x4 unityMatrix)
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

        public static void Convert(ref Matrix4x4 matrix, out Matrix3x3 bepuMatrix)
        {
            bepuMatrix.M11 = (FP)matrix.m00;
            bepuMatrix.M12 = (FP)matrix.m01;
            bepuMatrix.M13 = (FP)matrix.m02;
            
            bepuMatrix.M21 = (FP)matrix.m10;
            bepuMatrix.M22 = (FP)matrix.m11;
            bepuMatrix.M23 = (FP)matrix.m12;
            
            bepuMatrix.M31 = (FP)matrix.m20;
            bepuMatrix.M32 = (FP)matrix.m21;
            bepuMatrix.M33 = (FP)matrix.m22;

        }

        //Quaternion
        public static Quaternion Convert(FPQuaternion quaternion)
        {
            Quaternion toReturn;
            toReturn.x = (float)quaternion.x;
            toReturn.y = (float)quaternion.y;
            toReturn.z = (float)quaternion.z;
            toReturn.w = (float)quaternion.w;
            return toReturn;
        }
        public static Quaternion ToQuaternion(this FPQuaternion quaternion)
        {
            Quaternion toReturn;
            toReturn.x = (float)quaternion.x;
            toReturn.y = (float)quaternion.y;
            toReturn.z = (float)quaternion.z;
            toReturn.w = (float)quaternion.w;
            return toReturn;
        }

        public static FPVector3 ToEuler(this FPQuaternion quaternion)
        {
            FP sqx = quaternion.x * quaternion.x;
            FP sqy = quaternion.y * quaternion.y;
            FP sqz = quaternion.z * quaternion.z;
            FP sqw = quaternion.w * quaternion.w;

            FPVector3 euler;
            euler.y = FP.Atan2(FP.C2 * (quaternion.w * quaternion.y - quaternion.x * quaternion.z), sqw - sqx - sqy + sqz);
            euler.x = FP.Atan2(FP.C2 * (quaternion.w * quaternion.x - quaternion.y * quaternion.z), sqw - sqx + sqy - sqz);
            euler.z = FP.Atan2(FP.C2 * (quaternion.w * quaternion.z - quaternion.x * quaternion.y), sqw + sqx - sqy - sqz);
            return euler;
        }
        public static FPQuaternion Convert(Quaternion quaternion)
        {
            FPQuaternion toReturn;
            toReturn.x = (FP)quaternion.x;
            toReturn.y = (FP)quaternion.y;
            toReturn.z = (FP)quaternion.z;
            toReturn.w = (FP)quaternion.w;
            return toReturn;
        }

        public static FPQuaternion ToFPQuaternion(this Quaternion quaternion)
        {
            FPQuaternion toReturn;
            toReturn.x = (FP)quaternion.x;
            toReturn.y = (FP)quaternion.y;
            toReturn.z = (FP)quaternion.z;
            toReturn.w = (FP)quaternion.w;
            return toReturn;
        }

        public static void Convert(ref FPQuaternion bepuQuaternion, out Quaternion quaternion)
        {
            quaternion.x = (float)bepuQuaternion.x;
            quaternion.y = (float)bepuQuaternion.y;
            quaternion.z = (float)bepuQuaternion.z;
            quaternion.w = (float)bepuQuaternion.w;
        }

        public static void Convert(ref Quaternion quaternion, out  FPQuaternion bepuQuaternion)
        {
            bepuQuaternion.x = (FP)quaternion.x;
            bepuQuaternion.y = (FP)quaternion.y;
            bepuQuaternion.z = (FP)quaternion.z;
            bepuQuaternion.w = (FP)quaternion.w;
        }

        //Ray
        public static FPRay Convert(Ray ray)
        {
            FPRay toReturn;
            
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out toReturn.Position);
            Convert(ref direction, out toReturn.Direction);
            
            return toReturn;
        }

        public static void Convert(ref Ray ray, out FPRay bepuRay)
        {
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out bepuRay.Position);
            Convert(ref direction, out bepuRay.Direction);
        }

        public static Ray Convert(FPRay ray)
        {
            Convert(ref ray.Position, out var position);
            Convert(ref ray.Direction, out var direction);
            return new Ray(position, direction);
        }

        public static void Convert(ref FPRay ray, out Ray unityRay)
        {
            Convert(ref ray.Position, out var pos);
            Convert(ref ray.Direction, out var dir);
            
            unityRay = new Ray(pos, dir);
        }

        //BoundingBox
        public static Bounds Convert(FPBoundingBox boundingBox)
        {
            Bounds toReturn = new();
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            toReturn.SetMinMax(min, max);
            return toReturn;
        }

        public static FPBoundingBox Convert(Bounds boundingBox)
        {
            FPBoundingBox toReturn;
            var min = boundingBox.min;
            var max = boundingBox.max;
            Convert(ref min, out toReturn.Min);
            Convert(ref max, out toReturn.Max);
            return toReturn;
        }

        public static void Convert(ref FPBoundingBox boundingBox, out Bounds unityBoundingBox)
        {
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            
            unityBoundingBox = new Bounds();
            unityBoundingBox.SetMinMax(min, max);
        }

        public static void Convert(ref Bounds boundingBox, out FPBoundingBox bepuBoundingBox)
        {
            var min = boundingBox.min;
            var max = boundingBox.max;
            
            Convert(ref min, out bepuBoundingBox.Min);
            Convert(ref max, out bepuBoundingBox.Max);
        }

        //Plane
        public static Plane Convert(FPPlane plane)
        {
            Convert(ref plane.Normal, out var normal);
            return new Plane(normal, (float)plane.D);
        }

        public static FPPlane Convert(Plane plane)
        {
            FPPlane toReturn;
            var normal = plane.normal;
            Convert(ref normal, out toReturn.Normal);
            toReturn.D = (FP)plane.distance;
            return toReturn;
        }

        public static void Convert(ref FPPlane plane, out Plane unityPlane)
        {
            Convert(ref plane.Normal, out var normal);
            unityPlane = new Plane(normal, (float)plane.D);
        }

        public static void Convert(ref Plane plane, out FPPlane bepuPlane)
        {
            var normal = plane.normal;
            Convert(ref normal, out bepuPlane.Normal);
            bepuPlane.D = (FP)plane.distance;
        }
    }
}
