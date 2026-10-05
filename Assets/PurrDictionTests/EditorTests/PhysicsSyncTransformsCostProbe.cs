using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Measures what a rollback query pays for syncing physics transforms before casting: the
    /// first query in a tick syncs whatever moved that tick, later ones find nothing dirty.
    /// </summary>
    public sealed class PhysicsSyncTransformsCostProbe
    {
        private const int Iterations = 200;

        [Test, Category("Benchmark")]
        public void SyncTransformsCostByDirtyColliderCount()
        {
            var report = new StringBuilder();
            var objects = new List<GameObject>();
            try
            {
                foreach (int count in new[] { 0, 100, 1000 })
                {
                    for (int i = 0; i < count; i++)
                    {
                        var go = new GameObject($"probe {i}");
                        go.AddComponent<BoxCollider>();
                        go.transform.position = new Vector3(i * 3f, 0f, 0f);
                        objects.Add(go);
                    }

                    Physics.SyncTransforms();
                    for (int warm = 0; warm < 5; warm++)
                    {
                        Move(objects, warm);
                        Physics.SyncTransforms();
                    }

                    double moveOnly = Median(Iterations, i => Move(objects, i));
                    double moveAndSync = Median(Iterations, i =>
                    {
                        Move(objects, i);
                        Physics.SyncTransforms();
                    });
                    double clean = Median(Iterations, _ => Physics.SyncTransforms());

                    report.Append($"dirty={count}: sync={moveAndSync - moveOnly:F1}us clean={clean:F1}us; ");

                    foreach (var go in objects)
                        Object.DestroyImmediate(go);
                    objects.Clear();
                }
            }
            finally
            {
                foreach (var go in objects)
                    Object.DestroyImmediate(go);
            }

            TestContext.Out.WriteLine(report.ToString());
            UnityEngine.Debug.Log($"[SyncTransformsCost] {report}");
            Assert.Pass(report.ToString());
        }

        private static void Move(List<GameObject> objects, int step)
        {
            float y = (step & 1) == 0 ? 0.01f : -0.01f;
            for (int i = 0; i < objects.Count; i++)
                objects[i].transform.position += new Vector3(0f, y, 0f);
        }

        private static double Median(int iterations, System.Action<int> action)
        {
            var samples = new List<double>(iterations);
            var watch = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                watch.Restart();
                action(i);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds * 1000d);
            }

            samples.Sort();
            return samples[samples.Count / 2];
        }
    }
}
