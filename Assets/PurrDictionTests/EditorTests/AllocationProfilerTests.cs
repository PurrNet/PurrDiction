using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.TestTools;

namespace PurrNet.Prediction.Tests.Editor
{
    // The heap counter misses small allocations that reuse freed slots, so these count every GC.Alloc
    // sample the profiler attributes to PurrNet or PurrDiction code while a server and a client run.
    public sealed class AllocationProfilerTests
    {
        [UnityTest]
        public IEnumerator SteadyPooledChurnThroughCoalescedFramesAllocatesNothing()
        {
            new PooledSpawnAllocationTests().RegisterPackers();
            using var world = new PooledSpawnAllocationTests.World(60);
            var live = new Queue<PredictedObjectID>();
            ulong tick = 11;
            for (int i = 1; i <= 60 * 30; i++, tick++)
                world.Churn(tick, live, deliver: i % 3 == 0);

            var frames = AssertTicksAllocateNothing(20, i => world.Churn(tick++, live, deliver: i == 3));
            while (frames.MoveNext())
                yield return frames.Current;
        }

        // Predicted projectiles make the topology the hierarchy copies into its histories swing in size.
        // Each copy took whichever pooled list was returned last and grew it, allocating a new backing
        // array several kilobytes long on most copies through a fight.
        [UnityTest]
        public IEnumerator PredictedShotsWithASwingingLiveCountAllocateNothing([Values(false, true)] bool mispredict)
        {
            SpawnChurnShooter.mispredict = mispredict;
            new PooledSpawnAllocationTests().RegisterPackers();
            using var world = new PooledSpawnAllocationTests.World(60, shooter: true);
            var live = new Queue<PredictedObjectID>();
            ulong tick = 11;
            world.server.hierarchy.Create(1, Vector3.zero, Quaternion.identity);
            // A cold pool keeps adding lists for each size the fight reaches until the histories have
            // cycled a few times over; it settles within about 40 s of ticks.
            int fewest = int.MaxValue, most = 0;
            for (int i = 1; i <= 60 * 60; i++, tick++)
            {
                world.Churn(tick, live, spawn: false, deliver: i % 3 == 0);
                world.LateUpdate();
                int count = world.client.hierarchy.currentState.spawnedPrefabs.Count;
                fewest = Math.Min(fewest, count);
                most = Math.Max(most, count);
            }
            Assert.That(most - fewest, Is.GreaterThan(20), "the client's live count must swing");

            var frames = AssertTicksAllocateNothing(60, i =>
            {
                world.Churn(tick++, live, spawn: false, deliver: i == 3);
                world.LateUpdate();
            });
            while (frames.MoveNext())
                yield return frames.Current;
        }

        // Runs frames of three ticks with allocation callstacks recorded, then fails on any allocation
        // attributed to product code.
        static IEnumerator AssertTicksAllocateNothing(int frames, Action<int> tick)
        {
            bool wasProfiling = ProfilerDriver.enabled;
            bool wasProfilingEditor = ProfilerDriver.profileEditor;
            bool hadCallstacks = UnityEngine.Profiling.Profiler.enableAllocationCallstacks;
            try
            {
                ProfilerDriver.ClearAllFrames();
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
                ProfilerDriver.profileEditor = true;
                ProfilerDriver.enabled = true;
                yield return null;
                yield return null;

                for (int frame = 0; frame < frames; frame++)
                {
                    for (int i = 1; i <= 3; i++)
                        tick(i);
                    yield return null;
                }

                ProfilerDriver.enabled = false;
                yield return null;

                var sites = CollectProductAllocations(out int profiledFrames);
                Assert.That(profiledFrames, Is.GreaterThan(0), "the profiler captured no frames");
                long total = sites.Values.Sum(s => s.bytes);
                var report = new StringBuilder($"{total} B allocated by product code over {profiledFrames} frames:");
                foreach (var pair in sites.OrderByDescending(p => p.Value.bytes).Take(10))
                    report.Append($"\n{pair.Value.bytes} B x{pair.Value.count}: {pair.Key}");
                Assert.That(total, Is.Zero, report.ToString());
            }
            finally
            {
                ProfilerDriver.enabled = wasProfiling;
                ProfilerDriver.profileEditor = wasProfilingEditor;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = hadCallstacks;
            }
        }

        // Keys each sample by its first resolved PurrNet frame. Generic methods over value types do not
        // resolve, so their allocations surface at the nearest named caller. Samples whose first
        // resolved frame is the test harness, NUnit or the editor are not product allocations.
        static Dictionary<string, (long bytes, int count)> CollectProductAllocations(out int frames)
        {
            var sites = new Dictionary<string, (long bytes, int count)>();
            var stack = new List<ulong>();
            frames = 0;
            for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frame, 0);
                if (!raw.valid)
                    continue;
                frames++;
                int gcAlloc = raw.GetMarkerId("GC.Alloc");
                for (int i = 0; i < raw.sampleCount; i++)
                {
                    if (raw.GetSampleMarkerId(i) != gcAlloc)
                        continue;
                    raw.GetSampleCallstack(i, stack);
                    string site = null;
                    for (int k = 0; k < stack.Count; k++)
                    {
                        var method = raw.ResolveMethodInfo(stack[k]);
                        var name = method.methodName;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("PurrNet"))
                        {
                            if (!string.IsNullOrEmpty(name) && (name.StartsWith("nunit") || name.Contains("UnityEngine.TestRunner")))
                                break;
                            continue;
                        }
                        if (!name.StartsWith("PurrNet.Prediction.EditorTests"))
                            site = $"{name}:{method.sourceFileLine}";
                        break;
                    }
                    if (site == null)
                        continue;
                    long size = raw.GetSampleMetadataCount(i) > 0 ? raw.GetSampleMetadataAsLong(i, 0) : 0;
                    sites.TryGetValue(site, out var agg);
                    sites[site] = (agg.bytes + size, agg.count + 1);
                }
            }
            return sites;
        }
    }
}
