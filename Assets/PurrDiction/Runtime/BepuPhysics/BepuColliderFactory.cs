using System;
using System.Collections.Generic;
using System.Linq;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Entities;
using BEPUphysics.Entities.Prefabs;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction
{
    public static class BepuColliderFactory
    {
        public static Entity Create(Transform transform, BepuColliderDefinition[] colliders, FP mass)
        {
            var entities = new CompoundShapeEntry[colliders.Length];
    
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                var shape = collider.type switch
                {
                    BepuColliderType.Sphere => new SphereShape(collider.radius),
                    BepuColliderType.Box => new BoxShape(collider.width, collider.height, collider.depth),
                    BepuColliderType.Capsule => new CapsuleShape(collider.radius, collider.height),
                    BepuColliderType.Mesh => CreateMeshShape(collider.mesh, transform.localToWorldMatrix, collider.convex),
                    _ => throw new ArgumentOutOfRangeException()
                };
        
                entities[i] = new CompoundShapeEntry(shape, new RigidTransform(FPVector3.zero, FPQuaternion.Identity), mass);
            }

            return new CompoundBody(entities, mass)
            {
                Position = transform.position.ToFPVector3(),
                Orientation = transform.rotation.ToFPQuaternion()
            };
        }
        
        private static EntityShape CreateMeshShape(Mesh mesh, Matrix4x4 worldMatrix, bool convex)
        {
            if (convex)
                return CreateConvexHullShape(mesh, worldMatrix);
            
            var vertices = MathConverter.Convert(mesh.vertices);
            var indices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                indices.AddRange(mesh.GetIndices(i));
            }

            var position = new Vector3(worldMatrix.m03, worldMatrix.m13, worldMatrix.m23);
            var bepuTransform = new AffineTransform(
                MathConverter.Convert(worldMatrix.lossyScale),
                MathConverter.Convert(worldMatrix.rotation),
                MathConverter.Convert(position)
            );

            return new MobileMeshShape(vertices, indices.ToArray(), bepuTransform, MobileMeshSolidity.DoubleSided);
        }

        private static EntityShape CreateConvexHullShape(Mesh mesh, Matrix4x4 worldMatrix)
        {
            var reducedVertices = GetConvexHullVertices(mesh, worldMatrix);
            return new ConvexHullShape(MathConverter.Convert(reducedVertices));
        }
        
        private static readonly Vector3[] SampleDirections = {
            Vector3.up, Vector3.down,
            Vector3.left, Vector3.right,
            Vector3.forward, Vector3.back,
            (Vector3.up + Vector3.forward).normalized,
            (Vector3.up + Vector3.back).normalized,
            (Vector3.down + Vector3.forward).normalized,
            (Vector3.down + Vector3.back).normalized,
            (Vector3.right + Vector3.forward).normalized,
            (Vector3.right + Vector3.back).normalized,
            (Vector3.left + Vector3.forward).normalized,
            (Vector3.left + Vector3.back).normalized
        };

        public static Vector3[] GetConvexHullVertices(Mesh mesh, Matrix4x4 worldMatrix, float tolerance = 0.15f)
        {
            var vertices = mesh.vertices;
            var reducedVertices = new List<Vector3>();
            var bounds = mesh.bounds;
            var center = bounds.center;

            foreach (var direction in SampleDirections)
            {
                float maxDot = float.MinValue;
                Vector3 extremeVertex = Vector3.zero;

                for (int i = 0; i < vertices.Length; i++)
                {
                    float dot = Vector3.Dot(vertices[i] - center, direction);
                    if (dot > maxDot)
                    {
                        maxDot = dot;
                        extremeVertex = vertices[i];
                    }
                }

                var localVertex = worldMatrix.MultiplyPoint3x4(extremeVertex);
        
                if (!reducedVertices.Any(v => Vector3.Distance(v, localVertex) < tolerance))
                {
                    reducedVertices.Add(localVertex);
                }
            }

            return reducedVertices.ToArray();
        }
        
        public static Mesh ConvertVerticesToMesh(IList<FPVector3> vertices)
        {
            Mesh mesh = new Mesh();
            mesh.vertices = vertices.Select(v => v.ToVector3()).ToArray();
    
            List<int> triangles = new List<int>();
            for (int i = 0; i < vertices.Count - 2; i++)
            {
                triangles.Add(0);
                triangles.Add(i + 1);
                triangles.Add(i + 2);
            }
    
            mesh.triangles = triangles.ToArray();
            return mesh;
        }

        public static void DrawGizmos(Transform transform, BepuColliderDefinition[] colliderDefinitions)
        {
            DrawGizmos(transform.position, transform.rotation, colliderDefinitions);
        }

        public static void DrawGizmos(Vector3 position, Quaternion rotation, BepuColliderDefinition[] colliderDefinitions)
        {
            Gizmos.color = Color.yellow;

            foreach (var collider in colliderDefinitions)
            {
                switch (collider.type)
                {
                    case BepuColliderType.Sphere:
                        Matrix4x4 sphereMatrix = Matrix4x4.TRS(position, rotation, Vector3.one);
                        Gizmos.matrix = sphereMatrix;
                        Gizmos.DrawWireSphere(Vector3.zero, (float)collider.radius);
                        Gizmos.matrix = Matrix4x4.identity;
                        break;
                    
                    case BepuColliderType.Box:
                        Vector3 boxSize = new Vector3((float)collider.width, (float)collider.height, (float)collider.depth);
                        Matrix4x4 boxMatrix = Matrix4x4.TRS(position, rotation, Vector3.one);
                        Gizmos.matrix = boxMatrix;
                        Gizmos.DrawWireCube(Vector3.zero, boxSize);
                        Gizmos.matrix = Matrix4x4.identity;
                        break;
                    
                    case BepuColliderType.Capsule:
                        float radius = (float)collider.radius;
                        float height = (float)collider.height;
                        Vector3 pointOffset = Vector3.up * (height * 0.5f - radius);
                        
                        Gizmos.DrawWireSphere(position + rotation * pointOffset, radius);
                        Gizmos.DrawWireSphere(position + rotation * -pointOffset, radius);
                        
                        Vector3[] directions = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
                        foreach (var dir in directions)
                        {
                            Gizmos.DrawLine(
                                position + rotation * (pointOffset + dir * radius),
                                position + rotation * (-pointOffset + dir * radius));
                        }
                        break;

                    case BepuColliderType.Mesh:
                        Gizmos.color = Color.cyan;
            
                        if (collider.convex)
                        {
                            var vertices = GetConvexHullVertices(collider.mesh, Matrix4x4.identity);
                            Matrix4x4 meshMatrix = Matrix4x4.TRS(position, rotation, Vector3.one);
                            Gizmos.matrix = meshMatrix;
                            
                            for (int i = 0; i < vertices.Length; i++)
                            {
                                for (int j = i + 1; j < vertices.Length; j++)
                                {
                                    Gizmos.DrawLine(vertices[i], vertices[j]);
                                }
                            }
                            
                            Gizmos.matrix = Matrix4x4.identity;
                        }
                        else 
                        {
                            var vertices = collider.mesh.vertices;
                            var triangles = collider.mesh.triangles;
                            
                            for (int i = 0; i < triangles.Length; i += 3)
                            {
                                Vector3 v1 = position + rotation * vertices[triangles[i]];
                                Vector3 v2 = position + rotation * vertices[triangles[i + 1]];
                                Vector3 v3 = position + rotation * vertices[triangles[i + 2]];
                                
                                Gizmos.DrawLine(v1, v2);
                                Gizmos.DrawLine(v2, v3);
                                Gizmos.DrawLine(v3, v1);
                            }
                        }
                        
                        Gizmos.color = Color.yellow;
                        break;
                }
            }
        }
    }
}
