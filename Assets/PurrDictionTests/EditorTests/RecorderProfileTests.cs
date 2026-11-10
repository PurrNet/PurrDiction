using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using PurrNet.Prediction.Profiler;
using PurrNet.Prediction.Recording;
using PurrNet.Recording;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Main-thread cost of <see cref="NetworkRecorder"/> on a server with many moving entities, pooled churn and a
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
            report.Append(h.Describe());
            TestContext.WriteLine(report);
            Debug.Log(report);
            Assert.That(allocations, Is.Zero, report.ToString());
        }

        /// <summary>
        /// Where the recorder allocates over a whole match, warm-up included: the live entity count ramps up (so
        /// pools and logs hit new high-water marks) with bullet-like churn on top. Prints allocations per 10 s window
        /// and splits chunk-flush ticks from the others. Allocation counts are the main thread's; heap growth (kept memory
        /// included) and collections are the whole process's, so compare them with record=false.
        /// </summary>
        [Explicit("profile")]
        [TestCase(30, 300, 150, true)]
        [TestCase(30, 300, 150, false)]
        public void AllocationsAcrossAMatch(int tickRate, int peakEntities, int seconds, bool record)
        {
            using var h = new RecorderHarness(tickRate, prefab =>
            {
                prefab.AddComponent<BoxCollider>();
                var body = new GameObject("Body");
                body.transform.SetParent(prefab.transform, false);
                body.AddComponent<MeshRenderer>();
            });
            var world = h.world;
            var standing = new List<GameObject>();
            var bullets = new Queue<PredictedObjectID>();
            h.Start($"alloc-{tickRate}hz-{peakEntities}e.purrrec");
            var chunksField = typeof(RecordingSession).GetField("_chunks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            int ticks = tickRate * seconds, window = tickRate * 10;
            GC.Collect();
            var report = new StringBuilder($"[RecorderAllocations] record={record} {tickRate} Hz, ramp to {peakEntities} entities over {seconds / 2}s, {seconds}s\n");
            long winCount = 0, winBytes = 0, flushCount = 0, flushBytes = 0, otherCount = 0, otherBytes = 0;
            int winTicks = 0, winCollections = GC.CollectionCount(0);
            for (int i = 0; i < ticks; i++, h.tick++)
            {
                ulong tick = h.tick;
                int target = Math.Min(peakEntities, peakEntities * i / (ticks / 2) + 1);
                while (standing.Count < target)
                {
                    var id = world.server.hierarchy.Create(0, new Vector3(standing.Count, 1, 0), Quaternion.identity)!.Value;
                    world.server.hierarchy.TryGetGameObject(id, out var go);
                    standing.Add(go);
                }
                for (int e = 0; e < standing.Count; e++)
                    standing[e].transform.SetPositionAndRotation(new Vector3(e + Mathf.Sin(tick * 0.05f + e), 1, 0), Quaternion.Euler(0, tick * 3f + e, 0));
                for (int b = 0; b < 3; b++)
                    bullets.Enqueue(world.server.hierarchy.Create(0, new Vector3(b, 0, 0), Quaternion.identity)!.Value);
                while (bullets.Count > 60)
                    world.server.hierarchy.Delete(bullets.Dequeue());

                world.StepServer(tick);
                int chunksBefore = (int)chunksField!.GetValue(h.recorder.session);
                long heapBefore = GC.GetTotalMemory(false);
                var gc = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 4096, ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                if (record)
                    h.Record(tick);
                gc.Stop();
                long bytes = Math.Max(0, GC.GetTotalMemory(false) - heapBefore);
                long count = gc.Count;
                gc.Dispose();
                bool flushed = (int)chunksField.GetValue(h.recorder.session) != chunksBefore;
                if (flushed) { flushCount += count; flushBytes += bytes; }
                else { otherCount += count; otherBytes += bytes; }
                winCount += count; winBytes += bytes;
                if (count > 0) winTicks++;
                if ((i + 1) % window == 0)
                {
                    report.Append($"  {(i + 1 - window) / tickRate,3}-{(i + 1) / tickRate,3}s  live {standing.Count + bullets.Count,4}  allocs {winCount,6} in {winTicks,3} ticks  heap +{winBytes / 1024.0,8:0.0} KiB  collections {GC.CollectionCount(0) - winCollections}\n");
                    winCollections = GC.CollectionCount(0);
                    winCount = winBytes = 0;
                    winTicks = 0;
                }

                world.Deliver(tick);
                TickBandwidthProfiler.MarkEndOfTick();
                world.LateUpdate();
            }
            report.Append($"  flush ticks: {flushCount} allocs, heap +{flushBytes / 1024.0:0.0} KiB; other ticks: {otherCount} allocs, heap +{otherBytes / 1024.0:0.0} KiB\n");
            h.Stop();
            report.Append(h.Describe());
            TestContext.WriteLine(report);
            Debug.Log(report);
        }

        /// <summary>File size and chunk count against the reserved chunk buffer (a full buffer ends the chunk early).</summary>
        [Explicit("profile")]
        [TestCase(256)]
        [TestCase(512)]
        public void ChunkBufferSizeVsFileSize(int entities)
        {
            var report = new StringBuilder($"[RecorderChunkBuffer] {entities} moving entities at 60 Hz, 40 s\n");
            foreach (int kb in new[] { 512, 1024, 2048, 4096, 8192 })
            {
                using var h = new RecorderHarness(60, prefab =>
                {
                    prefab.AddComponent<BoxCollider>();
                    var body = new GameObject("Body");
                    body.transform.SetParent(prefab.transform, false);
                    body.AddComponent<MeshRenderer>();
                });
                var standing = new List<GameObject>();
                for (int i = 0; i < entities; i++)
                {
                    var id = h.world.server.hierarchy.Create(0, new Vector3(i, 1, 0), Quaternion.identity)!.Value;
                    h.world.server.hierarchy.TryGetGameObject(id, out var go);
                    standing.Add(go);
                }
                h.Configure("_chunkBufferKilobytes", kb);
                string name = $"chunkbuffer-{entities}e-{kb}k.purrrec";
                h.Start(name);
                for (int i = 0; i < 60 * 40; i++)
                    h.Tick(() =>
                    {
                        for (int e = 0; e < standing.Count; e++)
                            standing[e].transform.SetPositionAndRotation(
                                new Vector3(e + Mathf.Sin(h.tick * 0.05f + e), 1, Mathf.Cos(h.tick * 0.05f + e)),
                                Quaternion.Euler(0, h.tick * 3f + e, 0));
                    });
                int chunks = (int)h.Private("_chunks");
                h.Stop();
                var file = new FileInfo(Path.Combine(h.directory, name));
                report.Append($"  {kb,5} KiB buffer: {file.Length / 1024.0,8:0} KiB file, {chunks,4} chunks\n");
            }
            TestContext.WriteLine(report);
            Debug.Log(report);
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
