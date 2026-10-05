using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PooledSpawnAllocationTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Lifetime = 10;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(SpawnChurnInput));
            Hasher.PrepareType(typeof(SpawnChurnState));
            Hasher.PrepareType(typeof(SpawnChurnProbe));
            Hasher.PrepareType(typeof(SpawnChurnAge));
            Hasher.PrepareType(typeof(SpawnChurnShooter));
            Hasher.PrepareType(typeof(SpawnChurnBullet));
            Hasher.PrepareType(typeof(PredictedHierarchyState));
            Hasher.PrepareType(typeof(InstanceDetails));
            Hasher.PrepareType(typeof(PredictedTransformState));
            Packer<SpawnChurnInput>.RegisterWriter((p, v) => Packer<int>.Write(p, v.amount));
            Packer<SpawnChurnInput>.RegisterReader((BitPacker p, ref SpawnChurnInput v) =>
                v.amount = Packer<int>.Read(p));
            Packer<SpawnChurnState>.RegisterWriter((p, v) => Packer<int>.Write(p, v.value));
            Packer<SpawnChurnState>.RegisterReader((BitPacker p, ref SpawnChurnState v) =>
                v.value = Packer<int>.Read(p));
            // Editor assemblies are not code-generated, so these states would fall back to a reader
            // that boxes disposable structs. Give them the delta packers a game's states get.
            DeltaPacker<SpawnChurnInput>.Register(
                (BitPacker p, SpawnChurnInput old, SpawnChurnInput value) => DeltaPacker<int>.Write(p, old.amount, value.amount),
                (BitPacker p, SpawnChurnInput old, ref SpawnChurnInput value) => DeltaPacker<int>.Read(p, old.amount, ref value.amount));
            Packer<SpawnChurnAge>.RegisterWriter((p, v) => Packer<int>.Write(p, v.ticks));
            Packer<SpawnChurnAge>.RegisterReader((BitPacker p, ref SpawnChurnAge v) =>
                v.ticks = Packer<int>.Read(p));
            DeltaPacker<SpawnChurnAge>.Register(
                (BitPacker p, SpawnChurnAge old, SpawnChurnAge value) => DeltaPacker<int>.Write(p, old.ticks, value.ticks),
                (BitPacker p, SpawnChurnAge old, ref SpawnChurnAge value) => DeltaPacker<int>.Read(p, old.ticks, ref value.ticks));
            DeltaPacker<SpawnChurnState>.Register(
                (BitPacker p, SpawnChurnState old, SpawnChurnState value) => DeltaPacker<int>.Write(p, old.value, value.value),
                (BitPacker p, SpawnChurnState old, ref SpawnChurnState value) => DeltaPacker<int>.Read(p, old.value, ref value.value));
        }

        // Every spawn used to bind fresh verified-history stores (eager arrays holding about 15 s of
        // ticks per component), and despawned ids kept theirs forever, so pooled churn still
        // produced tens of kilobytes of garbage per spawn on the server and on each client.
        [TestCase(20)]
        [TestCase(60)]
        public void SteadyStatePooledChurnRecyclesVerifiedStoresWithoutAllocating(int tickRate)
        {
            using var world = new World(tickRate);
            var live = new Queue<PredictedObjectID>();
            ulong tick = 11;

            // Warm every pool, history window and frame buffer past its first-use growth.
            for (int i = 0; i < tickRate * 30; i++, tick++)
                world.Churn(tick, live);

            Assert.That(world.LiveCount(world.client), Is.EqualTo(world.LiveCount(world.server)),
                "the client must materialize the churned objects for its side to be measured");
            int serverStores = world.server.verifiedStoreCount;
            int clientStores = world.client.verifiedStoreCount;

            int measured = tickRate * 10;
            var churn = world.Measure(ref tick, measured, live, true);
            Assert.That(world.server.verifiedStoreCount, Is.EqualTo(serverStores), "server stores leaked across spawns");
            Assert.That(world.client.verifiedStoreCount, Is.EqualTo(clientStores), "client stores leaked across spawns");
            var idle = world.Measure(ref tick, measured, live, false);

            double serverPerSpawn = (churn.server - idle.server) / measured;
            double clientPerSpawn = (churn.client - idle.client) / measured;
            var report = new System.Text.StringBuilder();
            report.Append($"{tickRate} Hz: server {serverPerSpawn:F0} B/spawn, client {clientPerSpawn:F0} B/spawn, ");
            report.Append($"stores {serverStores}/{clientStores}, recycled ");
            report.Append($"{world.server.verifiedHistoryRecycledStoresTotal}/{world.client.verifiedHistoryRecycledStoresTotal}; by phase:");
            for (int i = 0; i < World.PhaseNames.Length; i++)
                report.Append($" {World.PhaseNames[i]}={(churn.phases[i] - idle.phases[i]) / measured:F0}");
            TestContext.WriteLine(report);

            Assert.That(world.server.verifiedHistoryRecycledStoresTotal, Is.GreaterThan(0), report.ToString());
            Assert.That(world.client.verifiedHistoryRecycledStoresTotal, Is.GreaterThan(0), report.ToString());
            // The heap counter moves in whole blocks, so allow noise well below the old cost.
            Assert.That(serverPerSpawn, Is.LessThan(2048), report.ToString());
            Assert.That(clientPerSpawn, Is.LessThan(2048), report.ToString());
        }

        // Pool warmup only instantiates, so the first spawn of each warm instance used to allocate its
        // state and input histories, its view buffer and fresh verified stores, on every peer.
        [TestCase(20)]
        [TestCase(60)]
        public void FirstSpawnsOfWarmPooledInstancesReuseTheirPrewarmedBuffers(int tickRate)
        {
            const int warmup = 16;
            using var warm = new World(tickRate, warmup);
            var serverBuffers = warm.PooledBuffers(warm.server);
            var clientBuffers = warm.PooledBuffers(warm.client);
            Assert.That(serverBuffers.Count, Is.EqualTo(warmup * 2), "both identities on every warm instance");
            Assert.That(clientBuffers.Count, Is.EqualTo(warmup * 2));
            foreach (var buffers in serverBuffers.Values)
                Assert.That(buffers, Has.All.Not.Null, "warmup must size every per-instance buffer");

            ulong tick = 11;
            var spawned = warm.Burst(ref tick, warmup, out var warmBytes);

            AssertReusedPrewarmedBuffers(warm, warm.server, spawned, serverBuffers);
            AssertReusedPrewarmedBuffers(warm, warm.client, spawned, clientBuffers);
            Assert.That(warm.server.verifiedHistoryRecycledStoresTotal, Is.GreaterThanOrEqualTo(warmup * 2),
                "first spawns bind the verified stores seeded at warmup");
            Assert.That(warm.client.verifiedHistoryRecycledStoresTotal, Is.GreaterThanOrEqualTo(warmup * 2));

            // The same burst with the prewarm undone allocates what a first spawn used to.
            using var cold = new World(tickRate, warmup);
            cold.UndoPrewarm(cold.server);
            cold.UndoPrewarm(cold.client);
            ulong coldTick = 11;
            cold.Burst(ref coldTick, warmup, out var coldBytes);

            var report = $"{tickRate} Hz first spawn: server {warmBytes.server / warmup:F0} B (cold {coldBytes.server / warmup:F0} B), " +
                         $"client {warmBytes.client / warmup:F0} B (cold {coldBytes.client / warmup:F0} B)";
            TestContext.WriteLine(report);
            Assert.That(warmBytes.server, Is.LessThan(coldBytes.server / 4), report);
            Assert.That(warmBytes.client, Is.LessThan(coldBytes.client / 4), report);
        }

        static void AssertReusedPrewarmedBuffers(World world, PredictionManager manager,
            List<PredictedObjectID> spawned, Dictionary<PredictedIdentity, object[]> prewarmed)
        {
            foreach (var id in spawned)
            {
                Assert.That(manager.hierarchy.TryGetGameObject(id, out var instance), Is.True);
                foreach (var identity in instance.GetComponents<PredictedIdentity>())
                {
                    Assert.That(prewarmed.TryGetValue(identity, out var before), Is.True,
                        "spawns must take the warm instances");
                    var after = World.Buffers(identity);
                    for (int i = 0; i < before.Length; i++)
                        Assert.That(after[i], Is.SameAs(before[i]), $"{identity.GetType().Name} reallocated a prewarmed buffer");
                    Assert.That(Field(typeof(PredictedIdentity), "_moduleHistory").GetValue(identity), Is.Null,
                        "identities without dynamic modules never need a module-set history");
                }
            }
        }

        // A frame that covers several ticks (a lower server update rate, or frames held back by loss)
        // carries a lifecycle transcript. Reading it used to allocate a replay container per frame and a
        // closure per covered tick on every client.
        [TestCase(20)]
        [TestCase(60)]
        public void CoalescedFramesReadTheirLifecycleTranscriptWithoutAllocating(int tickRate)
        {
            const int every = 3;
            using var world = new World(tickRate);
            var live = new Queue<PredictedObjectID>();
            ulong tick = 11;

            for (int i = 1; i <= tickRate * 30; i++, tick++)
                world.Churn(tick, live, deliver: i % every == 0);

            Assert.That(world.LiveCount(world.client), Is.EqualTo(world.LiveCount(world.server)),
                "coalesced frames must still materialize every churned object");
            int frames = tickRate * 10;
            int storesBefore = world.client.verifiedStoreCount;
            double churnPerFrame = world.MeasureCoalescedClient(ref tick, frames, every, live) / frames;
            Assert.That(world.client.verifiedStoreCount, Is.EqualTo(storesBefore), "client stores leaked across coalesced frames");

            // Live objects keep changing state but the topology holds, isolating the transcript and
            // record readers from the hierarchy's own delta codec.
            double perFrame = world.MeasureCoalescedClient(ref tick, frames, every, live, false) / frames;
            var report = $"{tickRate} Hz, {every} ticks per frame: client {perFrame:F0} B/frame ({churnPerFrame:F0} while spawning)";
            TestContext.WriteLine(report);
            // The heap counter is process-wide and moves in whole blocks, so editor threads add a few
            // dozen bytes per frame; the old readers cost several hundred.
            Assert.That(perFrame, Is.LessThan(128), report);
        }

        internal sealed class World : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            private readonly List<NetworkManager> _networks = new();
            private readonly PredictedPrefabs _prefabs;
            private readonly List<PlayerPacker> _frames;
            private readonly PredictionManager.InputQueue _ack;
            private readonly PlayerID _recipient = new(7, false);
            private readonly double _previousCadence;
            private readonly int _tickRate;
            internal readonly PredictionManager server, client;

            internal World(int tickRate, int warmup = 0, bool shooter = false)
            {
                _tickRate = tickRate;
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                server = CreateWorld("Churn server", true);
                client = CreateWorld("Churn client", false);
                var prefab = NewObject("Churn prefab");
                prefab.AddComponent<PredictedTransform>();
                prefab.AddComponent<SpawnChurnProbe>();
                _prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
                _prefabs.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = true, warmupCount = warmup });
                if (shooter)
                {
                    var gun = NewObject("Churn shooter");
                    gun.AddComponent<SpawnChurnShooter>();
                    _prefabs.prefabs.Add(new PredictedPrefab { prefab = gun, pooled = true });
                    var bullet = NewObject("Churn bullet");
                    bullet.AddComponent<PredictedTransform>();
                    bullet.AddComponent<SpawnChurnBullet>();
                    _prefabs.prefabs.Add(new PredictedPrefab { prefab = bullet, pooled = true, warmupCount = warmup });
                }
                server.predictedPrefabs = _prefabs;
                client.predictedPrefabs = _prefabs;

                _frames = Get<List<PlayerPacker>>(server, "_clientFrames");
                _frames.Add(new PlayerPacker
                {
                    player = _recipient, packer = BitPackerPool.Get(),
                    requiresFullCheckpoint = true
                });
                _ack = new PredictionManager.InputQueue();
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(server, "_clientTicks").Add(_recipient, _ack);
                StepServer(10);
                Deliver(10);
                _ack.ackedServerTick = 10;
            }

            internal int LiveCount(PredictionManager manager) => Get<List<PredictedIdentity>>(manager, "_systems").Count;

            static readonly string[] BufferFields = { "_stateHistory", "_interpolatedState", "_inputHistory" };

            internal static object[] Buffers(PredictedIdentity identity)
            {
                var result = new List<object>();
                for (var type = identity.GetType(); type != null; type = type.BaseType)
                {
                    foreach (var name in BufferFields)
                    {
                        var field = type.GetField(name, Fields | BindingFlags.DeclaredOnly);
                        if (field != null)
                            result.Add(field.GetValue(identity));
                    }
                }
                return result.ToArray();
            }

            internal Dictionary<PredictedIdentity, object[]> PooledBuffers(PredictionManager manager)
            {
                var result = new Dictionary<PredictedIdentity, object[]>();
                foreach (var instance in Pooled(manager))
                foreach (var identity in instance.GetComponentsInChildren<PredictedIdentity>(true))
                    result.Add(identity, Buffers(identity));
                return result;
            }

            // Restores what warmup used to leave behind: no buffers and no seeded verified stores.
            internal void UndoPrewarm(PredictionManager manager)
            {
                foreach (var instance in Pooled(manager))
                foreach (var identity in instance.GetComponentsInChildren<PredictedIdentity>(true))
                {
                    for (var type = identity.GetType(); type != null; type = type.BaseType)
                    {
                        foreach (var name in BufferFields)
                            type.GetField(name, Fields | BindingFlags.DeclaredOnly)?.SetValue(identity, null);
                    }
                }
                ((System.Collections.IDictionary)Field(typeof(PredictionManager), "_recycledVerifiedStores").GetValue(manager)).Clear();
            }

            static List<GameObject> Pooled(PredictionManager manager)
            {
                var pooled = new List<GameObject>();
                var pools = Get<GameObjectPoolCollection>(manager, "_pools");
                var owned = (Dictionary<GameObject, GameObjectPool>)Field(typeof(GameObjectPoolCollection), "_pools").GetValue(pools);
                foreach (var pool in owned.Values)
                    pooled.AddRange((Stack<GameObject>)Field(typeof(GameObjectPool), "_pool").GetValue(pool));
                return pooled;
            }

            // Spawns count objects in one tick and delivers it, measuring each side's allocations.
            internal List<PredictedObjectID> Burst(ref ulong tick, int count, out (long server, long client) bytes)
            {
                var spawned = new List<PredictedObjectID>(count);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                int collections = GC.CollectionCount(0);
                long before = GC.GetTotalMemory(false);
                for (int i = 0; i < count; i++)
                    spawned.Add(server.hierarchy.Create(0, new Vector3(i, 0, 0), Quaternion.identity)!.Value);
                StepServer(tick);
                long mid = GC.GetTotalMemory(false);
                Deliver(tick);
                long after = GC.GetTotalMemory(false);
                TickBandwidthProfiler.MarkEndOfTick();
                Assert.That(GC.CollectionCount(0), Is.EqualTo(collections), "a collection ran during the burst");
                bytes = (mid - before, after - mid);
                tick++;
                return spawned;
            }

            internal void Churn(ulong tick, Queue<PredictedObjectID> live, bool spawn = true, bool deliver = true)
            {
                if (spawn)
                {
                    var id = server.hierarchy.Create(0, new Vector3(live.Count, 0, 0), Quaternion.identity);
                    live.Enqueue(id!.Value);
                    if (live.Count > Lifetime)
                        server.hierarchy.Delete(live.Dequeue());
                }

                StepServer(tick);
                if (deliver)
                    Deliver(tick);
                _ack.ackedServerTick = Get<ulong>(client, "_ackedServerTick");
                Invoke(server, "MaintainVerifiedStoreStorage");
                Invoke(client, "MaintainVerifiedStoreStorage");
                // OnPostTick clears the bandwidth profiler's per-tick records.
                TickBandwidthProfiler.MarkEndOfTick();
            }

            // PredictedHierarchy releases despawned instances held for rollback from LateUpdate, which
            // edit-mode tests never run.
            internal void LateUpdate()
            {
                HierarchyLateUpdate.Invoke(server.hierarchy, null);
                HierarchyLateUpdate.Invoke(client.hierarchy, null);
            }

            static readonly MethodInfo HierarchyLateUpdate = typeof(PredictedHierarchy).GetMethod("LateUpdate", Fields);

            // Measures only the client's frame application; the server keeps preparing a frame each
            // tick and sends every Nth, so each delivered frame covers that many ticks.
            internal double MeasureCoalescedClient(ref ulong tick, int frames, int every, Queue<PredictedObjectID> live, bool spawn = true)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long total = 0;
                int samples = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    for (int i = 1; i < every; i++, tick++)
                        Churn(tick, live, spawn, deliver: false);

                    if (spawn)
                    {
                        var id = server.hierarchy.Create(0, new Vector3(live.Count, 0, 0), Quaternion.identity);
                        live.Enqueue(id!.Value);
                        if (live.Count > Lifetime)
                            server.hierarchy.Delete(live.Dequeue());
                    }
                    StepServer(tick);
                    Deliver(tick);
                    if (lastClientApplyBytes >= 0)
                    {
                        total += lastClientApplyBytes;
                        samples++;
                    }
                    _ack.ackedServerTick = Get<ulong>(client, "_ackedServerTick");
                    Invoke(server, "MaintainVerifiedStoreStorage");
                    Invoke(client, "MaintainVerifiedStoreStorage");
                    TickBandwidthProfiler.MarkEndOfTick();
                    tick++;
                }
                Assert.That(samples, Is.GreaterThan(frames / 2), "too many collections to measure allocations");
                return total * (double)frames / samples;
            }

            internal static readonly string[] PhaseNames =
            {
                "create", "delete", "prepare", "saveEntering", "captureInput", "captureLifecycle",
                "capturePhysicsHierarchy", "writeFrame", "simulate", "capturePhysicsState", "writeEvents",
                "serverMaintain", "markSent", "clientHandle", "clientProcess", "clientMaintain"
            };

            const int ClientPhaseStart = 12;
            private readonly long[] _tickPhases = new long[PhaseNames.Length];
            private bool _trackPhases;
            private long _phaseMark;

            private void Phase(int index)
            {
                if (!_trackPhases)
                    return;
                long now = GC.GetTotalMemory(false);
                _tickPhases[index] += now - _phaseMark;
                _phaseMark = now;
            }

            // Unity's Mono has no per-thread allocation counter, so heap growth stands in for it.
            // Ticks where a collection ran are skipped and the totals are scaled back up.
            internal (double server, double client, double[] phases) Measure(ref ulong tick, int ticks, Queue<PredictedObjectID> live, bool spawn)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var totals = new double[PhaseNames.Length];
                int samples = 0;
                _trackPhases = true;
                for (int i = 0; i < ticks; i++, tick++)
                {
                    Array.Clear(_tickPhases, 0, _tickPhases.Length);
                    int collections = GC.CollectionCount(0);
                    _phaseMark = GC.GetTotalMemory(false);
                    if (spawn)
                    {
                        var id = server.hierarchy.Create(0, new Vector3(live.Count, 0, 0), Quaternion.identity);
                        live.Enqueue(id!.Value);
                        Phase(0);
                        if (live.Count > Lifetime)
                            server.hierarchy.Delete(live.Dequeue());
                        Phase(1);
                    }
                    StepServer(tick);
                    _ack.ackedServerTick = Get<ulong>(client, "_ackedServerTick");
                    Invoke(server, "MaintainVerifiedStoreStorage");
                    Phase(11);
                    Deliver(tick);
                    Invoke(client, "MaintainVerifiedStoreStorage");
                    TickBandwidthProfiler.MarkEndOfTick();
                    Phase(15);
                    if (GC.CollectionCount(0) != collections)
                        continue;
                    samples++;
                    for (int k = 0; k < totals.Length; k++)
                        totals[k] += _tickPhases[k];
                }
                _trackPhases = false;
                Assert.That(samples, Is.GreaterThan(ticks / 2), "too many collections to measure allocations");
                double serverBytes = 0, clientBytes = 0;
                for (int k = 0; k < totals.Length; k++)
                {
                    totals[k] *= ticks / (double)samples;
                    if (k < ClientPhaseStart) serverBytes += totals[k];
                    else clientBytes += totals[k];
                }
                return (serverBytes, clientBytes, totals);
            }

            private void StepServer(ulong tick)
            {
                Set(server, "<localTick>k__BackingField", tick);
                Set(server, "<localTickInContext>k__BackingField", tick);
                Set(server, "<isVerified>k__BackingField", true);
                var systems = Get<List<PredictedIdentity>>(server, "_systems");
                for (var i = 0; i < systems.Count; i++)
                {
                    systems[i].RunGetLatestUnityState();
                    systems[i].PrepareInput(true, true, tick, false);
                }
                Phase(2);
                Invoke(server, "SaveEnteringState", tick);
                Phase(3);
                Invoke(server, "CaptureInputHistory", tick);
                Phase(4);
                Invoke(server, "CaptureLifecycleHistory", tick);
                Phase(5);
                Invoke(server, "CapturePhysicsEventHierarchy", tick);
                Phase(6);
                Invoke(server, "WriteInitialFrameToOthers");
                Phase(7);
                Invoke(server, "SimulateFrame", tick, SaveModeNone, PredictionPassKind.Forward);
                Phase(8);
                for (var i = 0; i < systems.Count; i++)
                    systems[i].lastVerifiedTick = tick;
                Invoke(server, "CapturePhysicsEventState", tick);
                Phase(9);
                Invoke(server, "WriteEventHandles");
                Set(server, "<isVerified>k__BackingField", false);
                Phase(10);
            }

            private static readonly object SaveModeNone =
                Enum.Parse(typeof(PredictionManager).GetNestedType("HistorySaveMode", BindingFlags.NonPublic), "None");

            private void Deliver(ulong tick)
            {
                var prepared = _frames[0];
                Assert.That(prepared.preparedFrameTick, Is.EqualTo(tick));
                ulong baseline = prepared.preparedBaselineTick;
                bool full = prepared.fullFrame;

                var copy = BitPackerPool.Get();
                var source = prepared.packer;
                int end = source.positionInBits;
                int bytes = source.ToByteData().length;
                source.ResetPositionAndMode(true);
                copy.WriteBits(source, bytes * 8);
                source.SetBitPosition(end);
                copy.ResetPositionAndMode(true);

                var visibility = Get<Dictionary<PlayerID, PlayerVisibilityTimeline>>(server, "_playerVisibility")[_recipient];
                Invoke(server, "HandleVisibilityFrameSent", _recipient, visibility, tick);
                Invoke(server, "MarkPendingVisibilityDeletesSent", _recipient, tick);
                Invoke(server, "CommitPreparedDesyncHeals", _recipient);
                if (prepared.fullFrame)
                    prepared.BeginFullFrame(tick);
                prepared.fullFrame = false;
                prepared.sentVisibilityTick = tick;
                prepared.lastSentFrameTick = tick;
                prepared.preparedVisibilityTick = 0;
                prepared.preparedFrameTick = 0;
                _frames[0] = prepared;
                Phase(12);

                Set(client, "<localTick>k__BackingField", tick + 3);
                Set(client, "<localTickInContext>k__BackingField", tick + 3);
                Set(client, "_nextHistoryResyncRequestAt", double.PositiveInfinity);
                // Bound delegates keep reflection and boxing out of the measured client work.
                _handleFrame ??= (HandleFrameFromServerCall)Delegate.CreateDelegate(typeof(HandleFrameFromServerCall),
                    client, typeof(PredictionManager).GetMethod("HandleFrameFromServer", Fields));
                _processQueuedFrames ??= (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>),
                    client, typeof(PredictionManager).GetMethod("ProcessQueuedFrames", Fields));
                int collections = GC.CollectionCount(0);
                long before = GC.GetTotalMemory(false);
                _handleFrame(tick, baseline, tick, full, false, default, false, default, new BitPackerWithLength(bytes, copy));
                Phase(13);
                _processQueuedFrames(true);
                lastClientApplyBytes = GC.CollectionCount(0) == collections ? GC.GetTotalMemory(false) - before : -1;
                Phase(14);
            }

            private delegate void HandleFrameFromServerCall(ulong serverTick, ulong baselineTick, ulong inputAck,
                bool fullFrame, bool hasInputMargin, PackedInt inputMargin, bool hasInputSlack, PackedInt inputSlackMs,
                BitPackerWithLength delta);

            private HandleFrameFromServerCall _handleFrame;
            private Action<bool> _processQueuedFrames;

            // Heap growth while the client handled the last delivered frame, or -1 if a collection ran.
            internal long lastClientApplyBytes;

            private PredictionManager CreateWorld(string name, bool asServer)
            {
                var network = NewObject(name + " network").AddComponent<NetworkManager>();
                _networks.Add(network);
                Set(typeof(NetworkManager), network, asServer ? "<isServer>k__BackingField" : "<isClient>k__BackingField", true);
                var ticks = new TickManager(_tickRate, network, null, asServer);
                Set(typeof(NetworkManager), network, asServer ? "_serverTickManager" : "_clientTickManager", ticks);
                var manager = NewObject(name).AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", network);
                manager.SetIsSpawned(true, asServer);
                Set(manager, "<cachedIsServer>k__BackingField", asServer);
                Set(manager, "<tickRate>k__BackingField", _tickRate);
                Set(manager, "<tickDelta>k__BackingField", 1f / _tickRate);
                Set(manager, "<localTick>k__BackingField", 10UL);
                Set(manager, "<localTickInContext>k__BackingField", 10UL);
                Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(manager, "_updateViewMode", UpdateViewMode.None);
                var hierarchy = manager.RegisterSystem<PredictedHierarchy>();
                Set(manager, "<hierarchy>k__BackingField", hierarchy);
                return manager;
            }

            private GameObject NewObject(string name)
            {
                var go = new GameObject(name);
                _objects.Add(go);
                return go;
            }

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                foreach (var manager in new[] { client, server })
                {
                    if (!manager)
                        continue;
                    if (manager.hierarchy)
                        manager.hierarchy.Cleanup();
                    var systems = Get<List<PredictedIdentity>>(manager, "_systems");
                    foreach (var identity in systems)
                        identity.ReleasePredictionStateForPool();
                    systems.Clear();
                    Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Clear();
                    Set(manager, "_systemsCount", 0);
                    Set(manager, "<hierarchy>k__BackingField", null);
                    Invoke(manager, "CleanupAllSystems");
                    manager.SetIsSpawned(false, true);
                    manager.SetIsSpawned(false, false);
                    var pools = Get<GameObjectPoolCollection>(manager, "_pools");
                    if (pools != null)
                    {
                        var ownedPools = (Dictionary<GameObject, GameObjectPool>)Field(
                            typeof(GameObjectPoolCollection), "_pools").GetValue(pools);
                        foreach (var pool in ownedPools.Values)
                        {
                            var objects = (Stack<GameObject>)Field(typeof(GameObjectPool), "_pool").GetValue(pool);
                            while (objects.Count > 0)
                            {
                                var pooledObject = objects.Pop();
                                if (pooledObject) Object.DestroyImmediate(pooledObject);
                            }
                        }
                    }
                    pools?.Dispose();
                    Set(manager, "_pools", null);
                    var parent = Get<GameObject>(manager, "_poolParent");
                    if (parent)
                        Object.DestroyImmediate(parent);
                }
                foreach (var network in _networks)
                {
                    Set(typeof(NetworkManager), network, "<isServer>k__BackingField", false);
                    Set(typeof(NetworkManager), network, "<isClient>k__BackingField", false);
                }
                for (var i = _objects.Count - 1; i >= 0; i--)
                    if (_objects[i]) Object.DestroyImmediate(_objects[i]);
                if (_prefabs) Object.DestroyImmediate(_prefabs);
            }
        }

        private static T Get<T>(PredictionManager manager, string name) => (T)Field(typeof(PredictionManager), name).GetValue(manager);
        private static void Set(PredictionManager manager, string name, object value) => Set(typeof(PredictionManager), manager, name, value);
        private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
        private static FieldInfo Field(Type type, string name)
        {
            var result = type.GetField(name, Fields);
            Assert.That(result, Is.Not.Null, $"Missing field {type.Name}.{name}");
            return result;
        }
        private static object Invoke(PredictionManager manager, string name, params object[] args)
        {
            var method = typeof(PredictionManager).GetMethod(name, Fields);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            try { return method.Invoke(manager, args); }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    public struct SpawnChurnInput : IPredictedData
    {
        public int amount;
        public void Dispose() { }
    }

    public struct SpawnChurnState : IPredictedData<SpawnChurnState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class SpawnChurnProbe : PredictedIdentity<SpawnChurnInput, SpawnChurnState>
    {
        protected override void GetFinalInput(ref SpawnChurnInput input) => input.amount = 1;

        protected override void Simulate(SpawnChurnInput input, ref SpawnChurnState state, float delta)
            => state.value += input.amount;
    }

    public struct SpawnChurnAge : IPredictedData<SpawnChurnAge>
    {
        public int ticks;
        public void Dispose() { }
    }

    // Fires in bursts on every peer, so a client predicts spawns while it replays and the live count
    // swings. With mispredict the server fires on other ticks, and the client undoes the shots it
    // predicted wrongly.
    public sealed class SpawnChurnShooter : PredictedIdentity<SpawnChurnAge>
    {
        internal static bool mispredict;

        protected override void Simulate(ref SpawnChurnAge state, float delta)
        {
            state.ticks++;
            bool firing = state.ticks / 40 % 2 == 0;
            if (mispredict)
                firing &= predictionManager.cachedIsServer ? state.ticks % 7 < 3 : state.ticks % 5 < 2;
            if (!firing)
                return;
            for (int i = 0; i < 2; i++)
                predictionManager.hierarchy.Create(2, new Vector3(state.ticks, i, 0), Quaternion.identity);
        }
    }

    public sealed class SpawnChurnBullet : PredictedIdentity<SpawnChurnAge>
    {
        private const int Lifetime = 30;

        protected override void Simulate(ref SpawnChurnAge state, float delta)
        {
            if (++state.ticks >= Lifetime)
                predictionManager.hierarchy.Delete(gameObject);
        }
    }
}
