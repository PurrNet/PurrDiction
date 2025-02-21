using System;
using BEPUphysics.NarrowPhaseSystems.Pairs;
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

    public struct BepuCollisionData
    {
        public GameObject other;
        public ContactCollection contacts;
        
        public BepuCollisionData(GameObject other, ContactCollection contacts)
        {
            this.other = other;
            this.contacts = contacts;
        }
    }
}