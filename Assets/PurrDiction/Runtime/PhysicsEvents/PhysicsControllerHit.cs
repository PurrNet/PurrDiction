using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PhysicsControllerHit : IPackedAuto
    {
        public Vector3 point;
        public Vector3 normal;
        public Vector3 moveDirection;
        public float moveLength;

#if UNITY_PHYSICS_3D
        public PhysicsControllerHit(ControllerColliderHit hit)
        {
            point = hit.point;
            normal = hit.normal;
            moveDirection = hit.moveDirection;
            moveLength = hit.moveLength;
        }
#endif

        public override string ToString()
            => $"{{point={point}, normal={normal}, moveDir={moveDirection}, moveLen={moveLength}}}";
    }
}
