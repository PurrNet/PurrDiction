using System;
using FixMath.NET;

namespace PurrNet.Prediction
{
    public enum BepuColliderType
    {
        Sphere,
        Box,
        Capsule,
        
    }
    
    [Serializable]
    public struct BepuColliderDefinition
    {
        public BepuColliderType type;
        public FP radius;
        public FP width;
        public FP height;
        public FP depth;
    }
}