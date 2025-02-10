using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Entities;
using BEPUutilities;
using ConversionHelper;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class BepuDebugger : MonoBehaviour
    {
        #if UNITY_EDITOR
        [SerializeField] private bool drawStaticMeshes = true;
        [SerializeField] private bool drawVelocity = true;
        [SerializeField] private bool drawDynamicColliders = true;

        [PurrReadOnly, SerializeField] private int totalEntities;
        
        private List<StaticMesh> staticMeshes = new List<StaticMesh>();
        private BEPUphysics.Space _space;
        
        private void Start()
        {
            _space = FindFirstObjectByType<PredictionManager>().physics;
        }
        
        public void RegisterStaticMesh(StaticMesh mesh)
        {
            if (mesh != null && !staticMeshes.Contains(mesh))
            {
                staticMeshes.Add(mesh);
            }
        }
        
        private void OnDrawGizmos()
        {
            if (_space == null) return;

            if (drawDynamicColliders)
                DrawDynamicColliders();

            if (drawStaticMeshes)
                DrawStaticMeshes();

            if (drawVelocity)
                DrawVelocity();
        }

        private void DrawDynamicColliders()
        {
            Gizmos.color = Color.yellow;
            totalEntities = _space.Entities.Count;

            foreach (var entity in _space.Entities)
            {
                var colliderDefinitions = ConvertEntityToColliderDefinitions(entity);
        
                if (colliderDefinitions != null)
                {
                    BepuColliderFactory.DrawGizmos(
                        entity.Position.ToVector3(), 
                        entity.Orientation.ToQuaternion(), 
                        colliderDefinitions
                    );
                }
            }
        }

        private BepuColliderDefinition[] ConvertEntityToColliderDefinitions(Entity entity)
        {
            if (entity?.CollisionInformation?.Shape == null)
            {
                Debug.LogWarning("Entity or its collision information is null");
                return null;
            }

            switch (entity.CollisionInformation.Shape)
            {
                case CompoundShape compoundShape:
                    return GetCompoundShapeDefinitions(compoundShape);;

                default:
                    Debug.LogWarning($"Unhandled shape type: {entity.CollisionInformation.Shape.GetType().Name}");
                    return null;
            }
        }

        private static BepuColliderDefinition[] GetCompoundShapeDefinitions(CompoundShape compoundShape)
        {
            var compoundDefinitions = new List<BepuColliderDefinition>();
            foreach (var shapeEntry in compoundShape.Shapes)
            {
                BepuColliderDefinition definition = shapeEntry.Shape switch
                {
                    BoxShape boxShape => new BepuColliderDefinition 
                    { 
                        type = BepuColliderType.Box, 
                        width = boxShape.Width, 
                        height = boxShape.Height, 
                        depth = boxShape.Length 
                    },
                    SphereShape sphereShape => new BepuColliderDefinition 
                    { 
                        type = BepuColliderType.Sphere, 
                        radius = sphereShape.Radius 
                    },
                    CapsuleShape capsuleShape => new BepuColliderDefinition 
                    { 
                        type = BepuColliderType.Capsule, 
                        radius = capsuleShape.Radius, 
                        height = capsuleShape.Length 
                    },
                    ConvexHullShape convexHull => new BepuColliderDefinition 
                    { 
                        type = BepuColliderType.Mesh, 
                        mesh = BepuColliderFactory.ConvertVerticesToMesh(convexHull.Vertices),
                        convex = true 
                    },
                    _ => default
                };

                if (!Equals(definition, default(BepuColliderDefinition)))
                {
                    compoundDefinitions.Add(definition);
                }
            }

            return compoundDefinitions.Count > 0 ? compoundDefinitions.ToArray() : null;
        }

        private void DrawVelocity()
        {
            Gizmos.color = Color.red;
            foreach (var entity in _space.Entities)
            {
                if (entity.LinearVelocity != FPVector3.zero)
                {
                    Gizmos.DrawRay(entity.Position.ToVector3(), entity.LinearVelocity.ToVector3() / 2);
                }
            }
        }

        private void DrawStaticMeshes()
        {
            Gizmos.color = Color.cyan;
            foreach (var staticMesh in staticMeshes)
            {
                var vertices = staticMesh.Mesh.Data.Vertices;
                var indices = staticMesh.Mesh.Data.Indices;

                for (int i = 0; i < indices.Length; i += 3)
                {
                    Vector3 v0 = MathConverter.Convert(vertices[indices[i]]);
                    Vector3 v1 = MathConverter.Convert(vertices[indices[i + 1]]);
                    Vector3 v2 = MathConverter.Convert(vertices[indices[i + 2]]);

                    Gizmos.DrawLine(v0, v1);
                    Gizmos.DrawLine(v1, v2);
                    Gizmos.DrawLine(v2, v0);
                }
            }
        }
        #endif
    }
}
