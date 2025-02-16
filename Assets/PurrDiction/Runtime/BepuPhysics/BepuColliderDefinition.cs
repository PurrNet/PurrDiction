using System;
using BEPUutilities;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction
{
    public enum BepuColliderType
    {
        Sphere,
        Box,
        Capsule,
        Mesh,
    }
    
    [Serializable]
    public struct BepuColliderDefinition
    {
        public BepuColliderType type;
        public FP radius;
        public FP width;
        public FP height;
        public FP depth;
        public Mesh mesh;
        public bool convex;
        public FPVector3 offset;
    }
}