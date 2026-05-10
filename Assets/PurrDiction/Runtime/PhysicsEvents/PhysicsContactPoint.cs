using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PhysicsContactPoint : IPackedAuto
    {
        public Vector3 point;
        public Vector3 normal;
        public float separation;

#if UNITY_PHYSICS_3D
        public PhysicsContactPoint(ContactPoint contact)
        {
            point = contact.point;
            normal = contact.normal;
            separation = contact.separation;
        }
#endif

        public override string ToString() => $"{{point={point}, normal={normal}, sep={separation}}}";
    }
}