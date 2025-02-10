using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionShapes.ConvexShapes;
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
                var position = entity.Position.ToVector3();
                var orientation = entity.Orientation.ToQuaternion();
                switch (entity.CollisionInformation.Shape)
                {
                    case BoxShape boxShape:
                        Gizmos.matrix = Matrix4x4.TRS(position, orientation, Vector3.one);
                        Gizmos.DrawWireCube(Vector3.zero, new Vector3(
                            (float)boxShape.Width, 
                            (float)boxShape.Height, 
                            (float)boxShape.Length
                        ));
                        Gizmos.matrix = Matrix4x4.identity;
                        break;

                    case SphereShape sphereShape:
                        Gizmos.DrawWireSphere(position, (float)sphereShape.Radius);
                        break;

                    case CapsuleShape capsuleShape:
                        var height = (float)capsuleShape.Length;
                        var radius = (float)capsuleShape.Radius;
                        var halfHeight = height * 0.5f;

                        Gizmos.DrawWireSphere(
                            position + orientation * Vector3.up * halfHeight, 
                            radius
                        );
                        Gizmos.DrawWireSphere(
                            position - orientation * Vector3.up * halfHeight, 
                            radius
                        );

                        Gizmos.DrawLine(
                            position + orientation * (Vector3.up * halfHeight + Vector3.right * radius),
                            position - orientation * (Vector3.up * halfHeight - Vector3.right * radius)
                        );
                        break;

                    case CompoundShape compoundShape:
                        foreach (var entry in compoundShape.Shapes)
                        {
                            var localTransform = entry.LocalTransform;
                            var localPosition = localTransform.Position.ToVector3();
                            var localOrientation = localTransform.Orientation.ToQuaternion();

                            switch (entry.Shape)
                            {
                                case BoxShape boxShape:
                                    Gizmos.matrix = Matrix4x4.TRS(
                                        position + orientation * localPosition, 
                                        orientation * localOrientation, 
                                        Vector3.one
                                    );
                                    Gizmos.DrawWireCube(Vector3.zero, 
                                        new Vector3(
                                            (float)boxShape.Width, 
                                            (float)boxShape.Height, 
                                            (float)boxShape.Length
                                        ));
                                    Gizmos.matrix = Matrix4x4.identity;
                                    break;

                                case SphereShape sphereShape:
                                    Gizmos.DrawWireSphere(
                                        position + orientation * localPosition, 
                                        (float)sphereShape.Radius
                                    );
                                    break;
                            }
                        }
                        break;
                }
            }
        }

        private void DrawVelocity()
        {
            Gizmos.color = Color.red;
            foreach (var entity in _space.Entities)
            {
                if (entity.LinearVelocity != FPVector3.zero)
                {
                    Gizmos.DrawRay(
                        entity.Position.ToVector3(), 
                        entity.LinearVelocity.ToVector3() / 2
                    );
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
