using FixMath.NET;
using UnityEngine;

namespace ConversionHelper
{
    public static class MathConverter
    {
        //Vector2
        public static Vector2 Convert(BEPUutilities.FPVector2 bepuVector)
        {
            Vector2 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.FPVector2 bepuVector, out Vector2 unityVector)
        {
            unityVector.x = (float)bepuVector.x;
            unityVector.y = (float)bepuVector.y;
        }

        public static BEPUutilities.FPVector2 Convert(Vector2 unityVector)
        {
            BEPUutilities.FPVector2 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            return toReturn;
        }

        public static void Convert(ref Vector2 unityVector, out BEPUutilities.FPVector2 bepuVector)
        {
            bepuVector.x = (FP)unityVector.x;
            bepuVector.y = (FP)unityVector.y;
        }

        //Vector3
        public static Vector3 Convert(BEPUutilities.FPVector3 bepuVector)
        {
            Vector3 toReturn;
            toReturn.x = (float)bepuVector.x;
            toReturn.y = (float)bepuVector.y;
            toReturn.z = (float)bepuVector.z;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.FPVector3 bepuVector, out Vector3 unityVector)
        {
            unityVector.x = (float)bepuVector.x;
            unityVector.y = (float)bepuVector.y;
            unityVector.z = (float)bepuVector.z;
        }

        public static BEPUutilities.FPVector3 Convert(Vector3 unityVector)
        {
            BEPUutilities.FPVector3 toReturn;
            toReturn.x = (FP)unityVector.x;
            toReturn.y = (FP)unityVector.y;
            toReturn.z = (FP)unityVector.z;
            return toReturn;
        }

        public static void Convert(ref Vector3 unityVector, out BEPUutilities.FPVector3 bepuVector)
        {
            bepuVector.x = (FP)unityVector.x;
            bepuVector.y = (FP)unityVector.y;
            bepuVector.z = (FP)unityVector.z;
        }

        public static Vector3[] Convert(BEPUutilities.FPVector3[] bepuVectors)
        {
            Vector3[] unityVectors = new Vector3[bepuVectors.Length];
            for (int i = 0; i < bepuVectors.Length; i++)
            {
                Convert(ref bepuVectors[i], out unityVectors[i]);
            }
            return unityVectors;

        }

        public static BEPUutilities.FPVector3[] Convert(Vector3[] unityVectors)
        {
            var bepuVectors = new BEPUutilities.FPVector3[unityVectors.Length];
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
        public static Quaternion Convert(BEPUutilities.FPQuaternion quaternion)
        {
            Quaternion toReturn;
            toReturn.x = (float)quaternion.x;
            toReturn.y = (float)quaternion.y;
            toReturn.z = (float)quaternion.z;
            toReturn.w = (float)quaternion.w;
            return toReturn;
        }

        public static BEPUutilities.FPQuaternion Convert(Quaternion quaternion)
        {
            BEPUutilities.FPQuaternion toReturn;
            toReturn.x = (FP)quaternion.x;
            toReturn.y = (FP)quaternion.y;
            toReturn.z = (FP)quaternion.z;
            toReturn.w = (FP)quaternion.w;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.FPQuaternion bepuQuaternion, out Quaternion quaternion)
        {
            quaternion.x = (float)bepuQuaternion.x;
            quaternion.y = (float)bepuQuaternion.y;
            quaternion.z = (float)bepuQuaternion.z;
            quaternion.w = (float)bepuQuaternion.w;
        }

        public static void Convert(ref Quaternion quaternion, out  BEPUutilities.FPQuaternion bepuQuaternion)
        {
            bepuQuaternion.x = (FP)quaternion.x;
            bepuQuaternion.y = (FP)quaternion.y;
            bepuQuaternion.z = (FP)quaternion.z;
            bepuQuaternion.w = (FP)quaternion.w;
        }

        //Ray
        public static BEPUutilities.FPRay Convert(Ray ray)
        {
            BEPUutilities.FPRay toReturn;
            
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out toReturn.Position);
            Convert(ref direction, out toReturn.Direction);
            
            return toReturn;
        }

        public static void Convert(ref Ray ray, out BEPUutilities.FPRay bepuRay)
        {
            var position = ray.origin;
            var direction = ray.direction;
            
            Convert(ref position, out bepuRay.Position);
            Convert(ref direction, out bepuRay.Direction);
        }

        public static Ray Convert(BEPUutilities.FPRay ray)
        {
            Convert(ref ray.Position, out var position);
            Convert(ref ray.Direction, out var direction);
            return new Ray(position, direction);
        }

        public static void Convert(ref BEPUutilities.FPRay ray, out Ray unityRay)
        {
            Convert(ref ray.Position, out var pos);
            Convert(ref ray.Direction, out var dir);
            
            unityRay = new Ray(pos, dir);
        }

        //BoundingBox
        public static Bounds Convert(BEPUutilities.FPBoundingBox boundingBox)
        {
            Bounds toReturn = new();
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            toReturn.SetMinMax(min, max);
            return toReturn;
        }

        public static BEPUutilities.FPBoundingBox Convert(Bounds boundingBox)
        {
            BEPUutilities.FPBoundingBox toReturn;
            var min = boundingBox.min;
            var max = boundingBox.max;
            Convert(ref min, out toReturn.Min);
            Convert(ref max, out toReturn.Max);
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.FPBoundingBox boundingBox, out Bounds unityBoundingBox)
        {
            Convert(ref boundingBox.Min, out var min);
            Convert(ref boundingBox.Max, out var max);
            
            unityBoundingBox = new Bounds();
            unityBoundingBox.SetMinMax(min, max);
        }

        public static void Convert(ref Bounds boundingBox, out BEPUutilities.FPBoundingBox bepuBoundingBox)
        {
            var min = boundingBox.min;
            var max = boundingBox.max;
            
            Convert(ref min, out bepuBoundingBox.Min);
            Convert(ref max, out bepuBoundingBox.Max);
        }

        //Plane
        public static Plane Convert(BEPUutilities.FPPlane plane)
        {
            Convert(ref plane.Normal, out var normal);
            return new Plane(normal, (float)plane.D);
        }

        public static BEPUutilities.FPPlane Convert(Plane plane)
        {
            BEPUutilities.FPPlane toReturn;
            var normal = plane.normal;
            Convert(ref normal, out toReturn.Normal);
            toReturn.D = (FP)plane.distance;
            return toReturn;
        }

        public static void Convert(ref BEPUutilities.FPPlane plane, out Plane unityPlane)
        {
            Convert(ref plane.Normal, out var normal);
            unityPlane = new Plane(normal, (float)plane.D);
        }

        public static void Convert(ref Plane plane, out BEPUutilities.FPPlane bepuPlane)
        {
            var normal = plane.normal;
            Convert(ref normal, out bepuPlane.Normal);
            bepuPlane.D = (FP)plane.distance;
        }
    }
}
