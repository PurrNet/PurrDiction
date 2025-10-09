using Jitter2;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using PurrNet.Prediction;
using UnityEngine;

namespace PurrNet.Jitter2
{
    public class TestJitter : MonoBehaviour
    {
        [SerializeField] private bool _multiThreaded;
        [SerializeField] private int _Xboxes = 20;
        [SerializeField] private int _Yboxes = 20;

        private World _world;
        private RigidBody _plane;

        private void Awake()
        {
            _world = new World();
            // _world.SubstepCount = 4;

            _plane = _world.CreateRigidBody();
            _plane.AddShape(new BoxShape(100, 1, 100));
            _plane.Position = new JVector(0, -0.5, 0);
            _plane.IsStatic = true;

            for(FP64 x = 0; x < _Xboxes; x++)
            {
                for(FP64 y = 0; y < _Yboxes; y++)
                {
                    var body = _world.CreateRigidBody();
                    body.AddShape(new BoxShape(1));
                    body.Position = new JVector(x * 2 - _Xboxes / 2, y * 2 + 5, 0);
                }
            }
            _world.DynamicTree.Optimize(_Xboxes);
        }

        static Matrix4x4 GetMatrix(RigidBody body)
        {
            var ori = JMatrix.CreateFromQuaternion(body.Orientation);
            var pos = body.Position;

            return new Matrix4x4(
                new Vector4(ori.M11.ToFloat(), ori.M21.ToFloat(), ori.M31.ToFloat(), 0),
                new Vector4(ori.M12.ToFloat(), ori.M22.ToFloat(), ori.M32.ToFloat(), 0),
                new Vector4(ori.M13.ToFloat(), ori.M23.ToFloat(), ori.M33.ToFloat(), 0),
                new Vector4(pos.X.ToFloat(), pos.Y.ToFloat(), pos.Z.ToFloat(), 1)
            );
        }

        private void FixedUpdate()
        {
            _world.Step(1.0f / 60.0f, _multiThreaded);
        }

        private void OnDrawGizmos()
        {
            if (_world == null) return;

            foreach (var body in _world.RigidBodies)
            {
                if (body == _world.NullBody) continue;

                var matrix = GetMatrix(body);
                Gizmos.matrix = matrix;
                Gizmos.DrawCube(Vector3.zero, Vector3.one);

                var pos = new Vector3(body.Position.X.ToFloat(), body.Position.Y.ToFloat(), body.Position.Z.ToFloat());
                Debug.DrawLine(pos, pos + Vector3.up * 0.5f);
            }
        }
    }
}
