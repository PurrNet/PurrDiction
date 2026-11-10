using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Prediction.Profiler;
using PurrNet.Prediction.Recording;
using PurrNet.Recording;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// The recorder stays out of the game's way: steady play allocates nothing on the main thread, and looking for
    /// collider changes keeps to its time budget without missing changes.
    /// </summary>
    public sealed class RecorderBudgetTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => new PooledSpawnAllocationTests().RegisterPackers();

        static void Decorate(GameObject prefab)
        {
            prefab.AddComponent<BoxCollider>();
            var body = new GameObject("Body");
            body.transform.SetParent(prefab.transform, false);
            body.AddComponent<MeshRenderer>();
        }

        [Test]
        public void SteadyPlayDoesNotAllocate()
        {
            using var h = new RecorderHarness(60, Decorate);
            var map = h.SceneObject("Map");
            for (int i = 0; i < 300; i++)
                h.SceneObject("Piece " + i, map.transform).AddComponent<BoxCollider>();
            var standing = new List<GameObject>();
            for (int i = 0; i < 64; i++)
            {
                var id = h.world.server.hierarchy.Create(0, new Vector3(i, 1, 0), Quaternion.identity)!.Value;
                h.world.server.hierarchy.TryGetGameObject(id, out var go);
                standing.Add(go);
            }
            // One-second chunks, so warming up covers several chunk flushes and the writer's reuse of logs.
            h.Configure("_chunkSeconds", 1f);
            h.Start("budget-steady.purrrec");

            var bullets = new Queue<PredictedObjectID>();
            long allocations = 0;
            int ticksWithAllocations = 0;
            for (int i = 0; i < 1200; i++)
            {
                bool measuring = i >= 600;
                ulong tick = h.tick;
                for (int e = 0; e < standing.Count; e++)
                    standing[e].transform.SetPositionAndRotation(new Vector3(e + Mathf.Sin(tick * 0.05f + e), 1, 0),
                        Quaternion.Euler(0, tick * 3f + e, 0));
                bullets.Enqueue(h.world.server.hierarchy.Create(0, Vector3.zero, Quaternion.identity)!.Value);
                if (bullets.Count > 10)
                    h.world.server.hierarchy.Delete(bullets.Dequeue());

                h.world.StepServer(tick);
                var gc = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1024,
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                h.Record(tick);
                gc.Stop();
                Assert.That(gc.Valid, Is.True, "GC allocation recording is unavailable");
                if (measuring && gc.Count > 0)
                {
                    allocations += gc.Count;
                    ticksWithAllocations++;
                }
                gc.Dispose();
                h.world.Deliver(tick);
                TickBandwidthProfiler.MarkEndOfTick();
                h.world.LateUpdate();
                h.tick++;
            }
            h.Stop();
            Assert.That(allocations, Is.Zero, $"{allocations} allocations in {ticksWithAllocations} ticks of steady play");
        }

        [Test]
        public void ChunksEncodedInStepsMatchOneEncode()
        {
            // Browsers encode a chunk over several frames; the bytes must not depend on where it was paused.
            using var h = new RecorderHarness(60, Decorate);
            var standing = new List<GameObject>();
            for (int i = 0; i < 64; i++)
            {
                var id = h.world.server.hierarchy.Create(0, new Vector3(i, 1, 0), Quaternion.identity)!.Value;
                h.world.server.hierarchy.TryGetGameObject(id, out var go);
                standing.Add(go);
            }
            h.Configure("_chunkSeconds", 100f);
            h.Start("stepwise-chunk.purrrec");
            var bullets = new Queue<PredictedObjectID>();
            for (int i = 0; i < 120; i++)
            {
                for (int e = 0; e < standing.Count; e++)
                    standing[e].transform.position = new Vector3(e + Mathf.Sin(h.tick * 0.05f + e), 1, 0);
                bullets.Enqueue(h.world.server.hierarchy.Create(0, Vector3.zero, Quaternion.identity)!.Value);
                if (bullets.Count > 10)
                    h.world.server.hierarchy.Delete(bullets.Dequeue());
                h.Tick();
            }

            // The chunk being collected, with the stream table a flush gives it (see RecordingSession.FlushChunk).
            const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance |
                                                           System.Reflection.BindingFlags.NonPublic;
            object Field(object target, string name) => target.GetType().GetField(name, Private | System.Reflection.BindingFlags.Public)!.GetValue(target);
            var job = Field(h.recorder.session, "_job");
            var live = (ChunkLog)Field(job, "log");
            var log = new ChunkLog();
            log.bytes.WriteBytes(live.bytes.data, 0, live.bytes.length);
            log.firstTick = (ulong)Field(h.recorder.session, "_chunkFirstTick");
            log.tickCount = h.tick - log.firstTick;
            foreach (var s in (List<StreamRows>)Field(h.recorder.session, "_slots"))
            {
                log.streams.Add(new ChunkStream
                {
                    id = s.id, layout = s.layout, forwardFill = s.forwardFill, baseColumns = s.baseColumns,
                    baseSlot = s.baseStream != null ? (int)Field(s.baseStream, "slot") : -1
                });
            }
            Assert.That(log.bytes.length, Is.GreaterThan(64 * 1024), "a chunk worth pausing");

            var whole = new ChunkEncoder().Encode(log);
            byte[] expected = whole.data.AsSpan(0, whole.length).ToArray();

            var stepped = new ChunkEncoder();
            stepped.Begin(log);
            int pauses = 0;
            // A deadline in the past: every call does the least it may and pauses.
            while (!stepped.Step(0))
                pauses++;
            byte[] actual = stepped.result.data.AsSpan(0, stepped.result.length).ToArray();

            Assert.That(pauses, Is.GreaterThan(20), "the chunk was encoded in many steps");
            Assert.That(actual, Is.EqualTo(expected));
            h.Stop();
        }

        [Test]
        public void ChangesAreFoundWithNoTimeBudget()
        {
            using var h = new RecorderHarness(60, Decorate);
            var map = h.SceneObject("Map");
            var pieces = new List<BoxCollider>();
            for (int i = 0; i < 3000; i++)
                pieces.Add(h.SceneObject("Piece " + i, map.transform).AddComponent<BoxCollider>());
            var entities = new List<GameObject>();
            for (int i = 0; i < 40; i++)
            {
                var id = h.world.server.hierarchy.Create(0, new Vector3(i, 0, 0), Quaternion.identity)!.Value;
                h.world.server.hierarchy.TryGetGameObject(id, out var go);
                entities.Add(go);
            }
            // No time at all: every tick still makes the minimum progress.
            h.Configure("_colliderCheckBudgetMs", 0f);
            h.Start("budget-zero.purrrec");
            h.Ticks(10);

            pieces[1234].enabled = false;
            entities[37].AddComponent<SphereCollider>();
            h.Ticks(600);
            var meta = h.Stop();

            var staticSets = meta.Where(m => m.kind == RecordKind.Colliders && m.json.StartsWith("{\"entity\":null"))
                .Select(m => Regex.Matches(m.json, "\\{\"shape\":").Count).ToList();
            Assert.That(staticSets, Is.EqualTo(new[] { 3000, 2999 }), "the static change is found");
            Assert.That(meta.Count(m => m.kind == RecordKind.Colliders && !m.json.StartsWith("{\"entity\":null") &&
                                        m.json.Contains("\"shape\":\"sphere\"")), Is.EqualTo(1),
                "the entity's new collider is found");
        }

        [Test]
        public void NativeDeflateMatchesDeflateStreamAndDoesNotAllocate()
        {
            var method = typeof(RecordingFile).Assembly.GetType("PurrNet.Recording.NativeDeflate")!
                .GetMethod("TryCompress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
            var compress = (Func<byte[], int, IntPtr, bool>)Delegate.CreateDelegate(typeof(Func<byte[], int, IntPtr, bool>), method);
            var random = new System.Random(1);
            var payload = new byte[300000];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251 < 120 ? random.Next(4) : random.Next(256));

            foreach (int length in new[] { payload.Length, 1, 0 })
            {
                var output = new RecordingBuffer(64);
                var handle = GCHandle.Alloc(output);
                try
                {
                    Assert.That(compress(payload, length, GCHandle.ToIntPtr(handle)), Is.True, "the zlib helper is unavailable");
                    using var expected = new MemoryStream();
                    using (var deflate = new DeflateStream(expected, System.IO.Compression.CompressionLevel.Optimal, true))
                        deflate.Write(payload, 0, length);
                    Assert.That(output.data.Take(output.length).ToArray(), Is.EqualTo(expected.ToArray()), $"{length} bytes");

                    // Warm, the output buffer already big enough: nothing allocates.
                    output.Clear();
                    var gc = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 16, ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                    compress(payload, length, GCHandle.ToIntPtr(handle));
                    gc.Stop();
                    long count = gc.Count;
                    gc.Dispose();
                    Assert.That(count, Is.Zero, "compressing allocated");
                }
                finally
                {
                    handle.Free();
                }
            }
        }

        [Test]
        public void MeshCollidersAreWrittenFromNativeMeshData()
        {
            var mesh = new Mesh { name = "Ramp" };
            mesh.SetVertices(new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 1.5f), new(0, 0, 2) });
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 1, 3, 2, 2, 3, 4 }, 1);
            using var h = new RecorderHarness(60);
            h.SceneObject("Ground").AddComponent<MeshCollider>().sharedMesh = mesh;
            h.Start("budget-mesh.purrrec");
            h.Ticks(5);
            h.Stop();

            // The encoding the managed path writes: id, name, vertices as little-endian floats, then 32-bit indices.
            var expected = new RecordingBuffer(256);
            expected.WriteVarUInt(1);
            expected.WriteString("Ramp");
            expected.WriteVarUInt(5);
            foreach (var v in mesh.vertices)
            {
                expected.WriteFloat(v.x);
                expected.WriteFloat(v.y);
                expected.WriteFloat(v.z);
            }
            var indices = mesh.GetTriangles(0).Concat(mesh.GetTriangles(1)).ToArray();
            expected.WriteVarUInt((ulong)indices.Length);
            foreach (int i in indices)
                expected.WriteInt32(i);

            var meshes = RecorderHarness.ReadPayloads(h.path, RecordKind.Mesh);
            Assert.That(meshes.Count, Is.EqualTo(1));
            Assert.That(meshes[0], Is.EqualTo(expected.data.Take(expected.length).ToArray()));
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
