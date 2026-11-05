using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using PurrNet.Prediction.Profiler;
using PurrNet.Prediction.Recording;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Main-thread cost of <see cref="PredictionRecorder"/> on a server with many moving entities, pooled churn and a
    /// scene full of static colliders, next to the server's own tick. Explicit: it is a profile, not a gate. Run it
    /// with -releaseCodeOptimization for timings that resemble a player; the recording lands in
    /// test-results/recorder-tests.
    /// </summary>
    public sealed class RecorderProfileTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => new PooledSpawnAllocationTests().RegisterPackers();

        [Explicit("profile")]
        [TestCase(60, 32, 500)]
        [TestCase(60, 256, 2000)]
        [TestCase(60, 512, 5000)]
        public void ProfileRecorderPerTick(int tickRate, int entities, int staticColliders)
        {
            using var h = new RecorderHarness(tickRate, prefab =>
            {
                prefab.AddComponent<BoxCollider>();
                var body = new GameObject("Body");
                body.transform.SetParent(prefab.transform, false);
                body.AddComponent<MeshRenderer>();
                body.AddComponent<SphereCollider>();
            });
            var world = h.world;
            var map = h.SceneObject("Map");
            for (int i = 0; i < staticColliders; i++)
            {
                var piece = h.SceneObject("Piece " + i, map.transform);
                piece.transform.localPosition = new Vector3(i % 100, 0, i / 100);
                piece.AddComponent<BoxCollider>();
                piece.AddComponent<MeshRenderer>();
            }

            var live = new Queue<PredictedObjectID>();
            var standing = new List<GameObject>();
            for (int i = 0; i < entities; i++)
            {
                var id = world.server.hierarchy.Create(0, new Vector3(i, 1, 0), Quaternion.identity)!.Value;
                world.server.hierarchy.TryGetGameObject(id, out var go);
                standing.Add(go);
            }

            string name = $"profile-{tickRate}hz-{entities}e-{staticColliders}c.purrrec";
            h.Start(name);

            int warmup = tickRate * 30;
            int measured = tickRate * 60;
            var serverMs = new double[measured];
            var recordMs = new double[measured];
            long allocations = 0;
            int ticksWithAllocations = 0;
            int collectionsBefore = 0;
            var clock = new Stopwatch();

            for (int i = 0; i < warmup + measured; i++, h.tick++)
            {
                ulong tick = h.tick;
                bool measuring = i >= warmup;
                if (i == warmup)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    collectionsBefore = GC.CollectionCount(0);
                }

                // Everything moves, so rows change every tick like a real match.
                for (int e = 0; e < standing.Count; e++)
                    standing[e].transform.SetPositionAndRotation(
                        new Vector3(e + Mathf.Sin(tick * 0.05f + e), 1, Mathf.Cos(tick * 0.05f + e)),
                        Quaternion.Euler(0, tick * 3f + e, 0));

                var spawned = world.server.hierarchy.Create(0, new Vector3(live.Count, 0, 0), Quaternion.identity);
                live.Enqueue(spawned!.Value);
                if (live.Count > 10)
                    world.server.hierarchy.Delete(live.Dequeue());

                clock.Restart();
                world.StepServer(tick);
                double server = clock.Elapsed.TotalMilliseconds;

                ProfilerRecorder gc = default;
                if (measuring)
                    gc = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 4096,
                        ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                clock.Restart();
                h.Record(tick);
                double recorded = clock.Elapsed.TotalMilliseconds;
                if (measuring)
                {
                    gc.Stop();
                    Assert.That(gc.Valid, Is.True, "GC allocation recording is unavailable");
                    if (gc.Count > 0)
                    {
                        allocations += gc.Count;
                        ticksWithAllocations++;
                    }
                    gc.Dispose();
                    serverMs[i - warmup] = server;
                    recordMs[i - warmup] = recorded;
                }

                world.Deliver(tick);
                TickBandwidthProfiler.MarkEndOfTick();
                world.LateUpdate();
            }

            int collections = GC.CollectionCount(0) - collectionsBefore;
            var stats = h.Private("_stats");
            var sections = (string)stats.GetType().GetMethod("Summary")!.Invoke(stats, new object[] { (ulong)(warmup + measured) });
            h.Stop();
            var file = new FileInfo(Path.Combine(h.directory, name));

            var report = new StringBuilder();
            report.Append($"[RecorderProfile] {tickRate} Hz, {entities} moving entities (+10 churning), {staticColliders} static colliders, {measured} ticks measured\n");
            report.Append($"  server tick (harness)  {Describe(serverMs)}\n");
            report.Append($"  recorder per tick      {Describe(recordMs)}\n");
            report.Append($"  recorder / server tick {Sum(recordMs) / Sum(serverMs) * 100:0.0}%\n");
            report.Append($"  recorder ms per second of play {Sum(recordMs) / (measured / (double)tickRate):0.00}\n");
            report.Append($"  GC.Alloc samples on main thread while recording: {allocations} in {ticksWithAllocations} ticks; gen0 collections {collections}\n");
            report.Append($"  file {file.Length / 1024.0:0} KiB for {warmup + measured} ticks\n");
            report.Append(sections);
            TestContext.WriteLine(report);
            Debug.Log(report);
            Assert.That(allocations, Is.Zero, report.ToString());
        }

        static double Sum(double[] values)
        {
            double sum = 0;
            foreach (var v in values) sum += v;
            return sum;
        }

        static string Describe(double[] values)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            double P(double p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
            return $"mean={Sum(values) / values.Length:0.000}ms p50={P(0.5):0.000}ms p99={P(0.99):0.000}ms p99.9={P(0.999):0.000}ms max={sorted[^1]:0.000}ms";
        }
    }
}
