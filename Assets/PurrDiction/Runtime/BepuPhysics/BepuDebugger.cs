using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Constraints.SolverGroups;
using BEPUphysics.Entities;
using BEPUutilities;
using ConversionHelper;
using FixMath.NET;
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
        [SerializeField] private bool drawJoints = true;

        [PurrReadOnly, SerializeField] private int totalEntities;
        
        private List<StaticMesh> _staticMeshes = new List<StaticMesh>();
        private List<BepuHingeJoint> _hingeJoints = new List<BepuHingeJoint>();
        private BEPUphysics.Space _space;
        
        private void Start()
        {
            _space = FindFirstObjectByType<PredictionManager>().physics;
        }
        
        public void RegisterStaticMesh(StaticMesh mesh)
        {
            if (mesh != null && !_staticMeshes.Contains(mesh))
            {
                _staticMeshes.Add(mesh);
            }
        }
        
        public void RegisterHingeJoint(BepuHingeJoint hingeJoint)
        {
            if (hingeJoint != null && !_hingeJoints.Contains(hingeJoint))
            {
                _hingeJoints.Add(hingeJoint);
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

            if (drawJoints)
                DrawJoints();
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
            foreach (var staticMesh in _staticMeshes)
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

        private void DrawJoints()
        {
            foreach (var joint in _hingeJoints)
            {
                if (!joint.initialized)
                    continue;
                DrawHingeJoint(joint.transform.position, joint.axis, joint.initialTestAxis, (float)joint.angleLimitation, joint.connectedBody?.transform);
            }
        }
        
        public static void DrawHingeJoint(
            Vector3 position, 
            Vector3 axis, 
            Vector3 testAxis, 
            float angleLimitation,
            Transform connectedBody = null)
        {
            Vector3 normalizedAxis = axis.normalized;
        
            if (!Application.isPlaying && connectedBody != null)
            {
                Vector3 toConnected = (connectedBody.position - position).normalized;
                testAxis = Vector3.Cross(normalizedAxis, toConnected).normalized;
                testAxis = Quaternion.AngleAxis(-90, normalizedAxis) * testAxis;
            }
        
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(position, normalizedAxis);
        
            Gizmos.color = Color.yellow;
            var minAngle = -(angleLimitation / 2);
            var maxAngle = angleLimitation / 2;
            Quaternion minRot = Quaternion.AngleAxis(minAngle, normalizedAxis);
            Quaternion maxRot = Quaternion.AngleAxis(maxAngle, normalizedAxis);
        
            Gizmos.DrawRay(position, minRot * testAxis);
            Gizmos.DrawRay(position, maxRot * testAxis);
        
            int segments = 20;
            float angleStep = (maxAngle - minAngle) / segments;
            Vector3 prev = position + minRot * testAxis;
        
            for(int i = 1; i <= segments; i++)
            {
                float angle = minAngle + (angleStep * i);
                Vector3 next = position + (Quaternion.AngleAxis(angle, normalizedAxis) * testAxis);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }

            if (connectedBody != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(position, new Vector3(connectedBody.position.x, position.y, connectedBody.position.z));
            }
        }
        #endif
    }
}
