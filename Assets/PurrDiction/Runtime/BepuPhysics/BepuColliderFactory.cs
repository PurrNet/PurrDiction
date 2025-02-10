using System;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction
{
    public static class BepuColliderFactory
    {
        public static CompoundShapeEntry[] Create(Transform transform, BepuColliderDefinition[] colliders)
        {
            var entities = new CompoundShapeEntry[colliders.Length];
            
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                EntityShape shape = collider.type switch
                {
                    BepuColliderType.Sphere => new SphereShape(collider.radius),
                    BepuColliderType.Box => new BoxShape(collider.width, collider.height, collider.depth),
                    BepuColliderType.Capsule => new CapsuleShape(collider.radius, collider.height),
                    _ => throw new ArgumentOutOfRangeException()
                };
                
                entities[i] = new CompoundShapeEntry(shape, new RigidTransform(transform.position.ToFPVector3(), transform.rotation.ToFPQuaternion()), F64.C1);
            }

            return entities;
        }

        public static void DrawGizmos(Transform transform, BepuColliderDefinition[] colliderDefinitions)
        {
            Gizmos.color = Color.green;
            var position = transform.position;
            var rotation = transform.rotation;

            foreach (var collider in colliderDefinitions)
            {
                switch (collider.type)
                {
                    case BepuColliderType.Sphere:
                        Gizmos.DrawWireSphere(position, (float)collider.radius);
                        break;
                    
                    case BepuColliderType.Box:
                        Gizmos.matrix = Matrix4x4.TRS(position, rotation, Vector3.one);
                        Gizmos.DrawWireCube(Vector3.zero, 
                            new Vector3((float)collider.width, (float)collider.height, (float)collider.depth));
                        Gizmos.matrix = Matrix4x4.identity;
                        break;
                    
                    case BepuColliderType.Capsule:
                        var pointOffset = Vector3.up * (float)(collider.height * (FP)0.5f - collider.radius);
                        
                        Gizmos.DrawWireSphere(position + rotation * pointOffset, (float)collider.radius);
                        Gizmos.DrawWireSphere(position + rotation * -pointOffset, (float)collider.radius);
                        
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.right * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.right * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.left * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.left * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.forward * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.forward * (float)collider.radius));
                        Gizmos.DrawLine(
                            position + rotation * (pointOffset + Vector3.back * (float)collider.radius),
                            position + rotation * (-pointOffset + Vector3.back * (float)collider.radius));
                        break;
                }
            }
        }
    }
}
