#if UNITY_PHYSICS_2D && UNITY_6000
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // A rollback restores a body inside a one-way platform without the contact that let it pass. These
    // compare a restored and resimulated run with the uninterrupted one, which is what the server shows.
    public class OneWayPlatformRollbackTests
    {
        private const float Dt = 1f / 60f;
        private const int Steps = 150;
        private const int Resim = 20;

        public enum Platform { Box, Edge }

        public enum Before
        {
            // The client's predicted body is away from the platform, so the restore has no contact to reuse.
            NoContact,
            // The client's predicted body is passing through, so a disabled contact survives the restore.
            PassingThrough
        }

        private SimulationMode2D _mode;
        private GameObject _platform;
        private GameObject _body;
        private Rigidbody2D _rb;
        private OneWayContactHistory2D _history;

        [SetUp]
        public void SetUp()
        {
            _mode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }

        [TearDown]
        public void TearDown()
        {
            _history?.Clear();
            if (_body) Object.DestroyImmediate(_body);
            if (_platform) Object.DestroyImmediate(_platform);
            Physics2D.simulationMode = _mode;
        }

        // Thick platform, apex below its top: the reported case.
        [TestCase(Platform.Box, 1.5f, 0.9f, 0.71f, Before.NoContact)]
        // Edge on the top surface, apex more than halfway through it but feet still below.
        [TestCase(Platform.Edge, 0f, 1f, 0.8f, Before.NoContact)]
        // Thin platform, apex below its top.
        [TestCase(Platform.Box, 0.25f, 1f, 0.09f, Before.NoContact)]
        // Thin platform crossed fully, landing on top.
        [TestCase(Platform.Box, 0.25f, 1f, 1.6f, Before.NoContact)]
        [TestCase(Platform.Box, 0.25f, 1f, 1.6f, Before.PassingThrough)]
        [TestCase(Platform.Box, 1.5f, 0.9f, 0.71f, Before.PassingThrough)]
        public void RestoredRunMatchesUninterruptedRun(Platform shape, float thickness, float bodySize, float apexHead,
            Before before)
        {
            Build(shape, thickness, bodySize);
            var (pos, vel) = RunOriginal(thickness, bodySize, apexHead);

            float worst = Compare(pos, vel, thickness, bodySize, before, true, out var log);
            Assert.That(worst, Is.LessThan(0.01f), log);
        }

        [Test]
        public void ReportedCaseDivergesWithoutHistory()
        {
            Build(Platform.Box, 1.5f, 0.9f);
            var (pos, vel) = RunOriginal(1.5f, 0.9f, 0.71f);

            float worst = Compare(pos, vel, 1.5f, 0.9f, Before.NoContact, false, out var log);
            Assert.That(worst, Is.GreaterThan(0.5f), log);
        }

        private void Build(Platform shape, float thickness, float bodySize)
        {
            _platform = new GameObject("one-way platform");
            Collider2D collider;
            if (shape == Platform.Edge)
            {
                var edge = _platform.AddComponent<EdgeCollider2D>();
                edge.points = new[] { new Vector2(-3f, 0f), new Vector2(3f, 0f) };
                collider = edge;
            }
            else
            {
                var box = _platform.AddComponent<BoxCollider2D>();
                box.size = new Vector2(6f, thickness);
                collider = box;
            }
            collider.usedByEffector = true;
            var effector = _platform.AddComponent<PlatformEffector2D>();
            effector.useOneWay = true;
            effector.surfaceArc = 180f;
            effector.rotationalOffset = 0f;

            _body = new GameObject("body");
            _body.AddComponent<BoxCollider2D>().size = Vector2.one * bodySize;
            _rb = _body.AddComponent<Rigidbody2D>();
            _history = new OneWayContactHistory2D(_rb, Steps + 1);
        }

        private float Top(float thickness) => thickness * 0.5f;

        private (Vector2[] pos, Vector2[] vel) RunOriginal(float thickness, float bodySize, float apexHead)
        {
            float half = bodySize * 0.5f;
            var start = new Vector2(0f, -2f);
            float g = -Physics2D.gravity.y * _rb.gravityScale;
            float v0 = Mathf.Sqrt(2f * g * (apexHead - (start.y + half)));

            SetPose(start, new Vector2(0f, v0));
            var pos = new Vector2[Steps + 1];
            var vel = new Vector2[Steps + 1];
            for (var i = 0; i <= Steps; i++)
            {
                pos[i] = _rb.position;
                vel[i] = _rb.linearVelocity;
                _history.Record((ulong)i);
                if (i < Steps)
                    Step();
            }
            return (pos, vel);
        }

        private float Compare(Vector2[] pos, Vector2[] vel, float thickness, float bodySize, Before before, bool useHistory,
            out string log)
        {
            float half = bodySize * 0.5f, top = Top(thickness);
            var sb = new StringBuilder();
            float worst = 0f;
            int checkedTicks = 0;

            // The first tick the body overlaps the platform, which is where its contact began on the server.
            int firstOverlap = -1;
            for (var i = 0; i <= Steps && firstOverlap < 0; i++)
            {
                if (Overlaps(pos[i], half, top))
                    firstOverlap = i;
            }
            Assert.That(firstOverlap, Is.GreaterThanOrEqualTo(0), "The jump never reaches the platform.");

            for (var k = 0; k + Resim <= Steps; k++)
            {
                if (!Overlaps(pos[k], half, top))
                    continue;

                // The client's predicted body before the frame arrives.
                SetPose(new Vector2(0f, -8f), Vector2.zero);
                Step();
                if (useHistory) _history.AfterStep();
                if (before == Before.PassingThrough)
                {
                    SetPose(pos[firstOverlap - 1], vel[firstOverlap - 1]);
                    Step();
                    Step();
                    if (useHistory) _history.AfterStep();
                }

                // The verified frame restores tick k and the client resimulates.
                SetPose(pos[k], vel[k]);
                if (useHistory) _history.Restore((ulong)k);
                float err = 0f;
                for (var j = 1; j <= Resim; j++)
                {
                    Step();
                    if (useHistory) _history.AfterStep();
                    err = Mathf.Max(err, Vector2.Distance(_rb.position, pos[k + j]));
                }

                checkedTicks++;
                worst = Mathf.Max(worst, err);
                if (err > 0.001f)
                    sb.AppendLine($"tick {k}: y={pos[k].y:F3} vy={vel[k].y:F2} -> err {err:F3}, " +
                                  $"end y {_rb.position.y:F3} vs {pos[k + Resim].y:F3}");
            }

            Assert.That(checkedTicks, Is.GreaterThan(0), "No restored tick overlapped the platform.");
            sb.Insert(0, $"checked {checkedTicks} overlapping ticks, worst {worst:F3}\n");
            log = sb.ToString();
            Debug.Log(log);
            return worst;
        }

        private static bool Overlaps(Vector2 p, float half, float top) => p.y + half > -top && p.y - half < top;

        private void SetPose(Vector2 position, Vector2 velocity)
        {
            _rb.position = position;
            _rb.rotation = 0f;
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = 0f;
            _body.transform.SetPositionAndRotation(position, Quaternion.identity);
            Physics2D.SyncTransforms();
        }

        private static void Step() => Physics2D.Simulate(Dt);
    }
}
#endif
