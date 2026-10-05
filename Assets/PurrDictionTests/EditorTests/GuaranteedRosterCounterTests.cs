using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InputHistoryRosterTests
    {
        private const BindingFlags Members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Hasher.PrepareType(typeof(EmptyState));
            Hasher.PrepareType(typeof(StatefulInputProbe));
            Hasher.PrepareType(typeof(DeterministicInputProbe));
            Hasher.PrepareType(typeof(NoInputHistoryProbe));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) =>
                    value.id = Packer<int>.Read(packer));
        }

        [Test]
        public void RegistrationTracksAllInputBearingIdentitiesAcrossDuplicateIdsAndRemoval()
        {
            var networkObject = new GameObject("Roster fuzz network");
            var managerObject = new GameObject("Roster fuzz manager");
            var slotObjects = new List<GameObject>();
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager =
                    CreateSpawnedPredictionManager(managerObject, networkManager);
                var systems = GetField<List<PredictedIdentity>>(
                    typeof(PredictionManager), manager, "_systems");

                var identities = new List<PredictedIdentity>();
                var objectIds = new List<PredictedObjectID>();
                var registerable = new List<bool>();

                void AddSlot(int kind, uint objectId, bool canRegister)
                {
                    var slotObject = new GameObject($"Roster slot {slotObjects.Count}");
                    slotObjects.Add(slotObject);
                    identities.Add(kind == 1 ? slotObject.AddComponent<DeterministicInputProbe>()
                        : kind == 0 ? (PredictedIdentity)slotObject.AddComponent<StatefulInputProbe>()
                        : slotObject.AddComponent<NoInputHistoryProbe>());
                    objectIds.Add(new PredictedObjectID(objectId));
                    registerable.Add(canRegister);
                }

                AddSlot(1, 600, true);
                AddSlot(1, 601, true);
                AddSlot(0, 602, true);
                AddSlot(0, 603, true);
                AddSlot(1, 600, true);
                AddSlot(0, 602, true);
                AddSlot(1, 606, false);
                AddSlot(0, 607, false);
                AddSlot(2, 608, true);
                AddSlot(2, 609, false);

                int RecountInputs()
                {
                    var count = 0;
                    for (var i = 0; i < systems.Count; i++)
                    {
                        if (systems[i].hasInput)
                            count++;
                    }
                    return count;
                }

                var rng = new System.Random(1337);
                int registers = 0;
                int duplicateRegisters = 0;
                int removals = 0;
                int guardedUnregisters = 0;

                for (var op = 0; op < 300; op++)
                {
                    var slot = rng.Next(identities.Count);
                    var identity = identities[slot];
                    bool contained = systems.Contains(identity);

                    if (registerable[slot] && rng.Next(2) == 0)
                    {
                        manager.RegisterInstance(
                            slotObjects[slot], objectIds[slot], null, false, false);
                        if (contained)
                            duplicateRegisters++;
                        else
                            registers++;
                    }
                    else
                    {
                        manager.UnregisterInstance(identity);
                        if (contained)
                            removals++;
                        else
                            guardedUnregisters++;
                    }

                    Assert.That(
                        manager.inputHistorySystems,
                        Is.EqualTo(RecountInputs()),
                        $"input counter diverged from the roster at op {op}");
                    Assert.That(
                        GetField<int>(typeof(PredictionManager), manager, "_systemsCount"),
                        Is.EqualTo(systems.Count),
                        $"systems count bookkeeping diverged at op {op}");
                }

                Assert.That(registers, Is.GreaterThan(0));
                Assert.That(duplicateRegisters, Is.GreaterThan(0));
                Assert.That(removals, Is.GreaterThan(0));
                Assert.That(guardedUnregisters, Is.GreaterThan(0));

                for (var i = 0; i < identities.Count; i++)
                    manager.UnregisterInstance(identities[i]);

                Assert.That(manager.inputHistorySystems, Is.Zero);
                Assert.That(systems.Count, Is.Zero);
            }
            finally
            {
                for (var i = slotObjects.Count - 1; i >= 0; i--)
                    Object.DestroyImmediate(slotObjects[i]);
                InputTranscriptFixture.DisposeManagerCaches(managerObject.GetComponent<PredictionManager>());
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void RegisteredOrdinaryAndDeterministicSystemsShareTheCompleteTranscript()
        {
            var networkObject = new GameObject("Transcript network");
            var managerObject = new GameObject("Transcript manager");
            var deterministicObject = new GameObject("Transcript deterministic identity");
            var ordinaryObject = new GameObject("Transcript ordinary identity");
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var deterministic = deterministicObject.AddComponent<DeterministicInputProbe>();
                var ordinary = ordinaryObject.AddComponent<StatefulInputProbe>();
                manager.RegisterInstance(deterministicObject, new PredictedObjectID(700), null, false, false);
                manager.RegisterInstance(ordinaryObject, new PredictedObjectID(701), null, false, false);
                Assert.That(manager.inputHistorySystems, Is.EqualTo(2));

                SetLocalTick(manager, 20);
                for (ulong tick = 16; tick <= 20; tick++)
                {
                    SeedInput(deterministic, tick, (int)tick);
                    SeedInput(ordinary, tick, (int)(100 + tick));
                    typeof(PredictionManager).GetMethod("CaptureInputHistory", Members)
                        .Invoke(manager, new object[] { tick });
                }
                using var frame = BitPackerPool.Get();
                WriteInputHistory(manager, frame, baselineTick: 15);
                var writtenBits = frame.positionInBits;
                frame.ResetPositionAndMode(true);
                Assert.That(Packer<PackedUInt>.Read(frame).value, Is.EqualTo(5));
                var previous = new List<(PredictedComponentID id, int value)>();
                for (ulong tick = 16; tick <= 20; tick++)
                {
                    Assert.That(Packer<PackedUInt>.Read(frame).value, Is.EqualTo(2), $"tick {tick}");
                    if (tick > 16)
                    {
                        Assert.That(Packer<bool>.Read(frame), Is.True, "the same two identities are present every tick");
                    }
                    Assert.That(Packer<PackedUInt>.Read(frame).value, Is.Zero, "no player view offsets were recorded");
                    var deltas = new bool[2];
                    if (tick > 16)
                    {
                        for (var record = 0; record < 2; record++)
                        {
                            Assert.That(Packer<bool>.Read(frame), Is.False, "changing inputs are never encoded as repeats");
                            deltas[record] = Packer<bool>.Read(frame);
                            if (!deltas[record])
                                Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                        }
                    }
                    frame.SkipBits((8 - frame.positionInBits % 8) % 8);
                    var values = new Dictionary<PredictedComponentID, int>();
                    for (var record = 0; record < 2; record++)
                    {
                        PredictedComponentID id;
                        if (tick > 16)
                            id = previous[record].id;
                        else
                        {
                            id = Packer<PredictedComponentID>.Read(frame);
                            Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                        }
                        int value;
                        if (deltas[record])
                        {
                            using var baseline = BitPackerPool.Get();
                            Packer<bool>.Write(baseline, true);
                            Packer<TrackedInput>.Write(baseline, new TrackedInput(previous[record].value));
                            using var decoded = BitPackerPool.Get();
                            InputHistoryDelta.Read(frame, writtenBits, new BitData(baseline), decoded);
                            decoded.ResetPositionAndMode(true);
                            Assert.That(Packer<bool>.Read(decoded), Is.True);
                            value = Packer<TrackedInput>.Read(decoded).id;
                        }
                        else value = ReadLengthPrefixedInput(frame).id;
                        values.Add(id, value);
                        if (tick == 16) previous.Add((id, value));
                        else previous[record] = (id, value);
                    }
                    Assert.That(values[deterministic.id], Is.EqualTo((int)tick));
                    Assert.That(values[ordinary.id], Is.EqualTo((int)(100 + tick)));
                }
                Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
            }
            finally
            {
                Object.DestroyImmediate(ordinaryObject);
                Object.DestroyImmediate(deterministicObject);
                InputTranscriptFixture.DisposeManagerCaches(managerObject.GetComponent<PredictionManager>());
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovingAnyInputBearingIdentityKeepsRetainedDeltaHistoryUsable(bool deterministic)
        {
            var networkObject = new GameObject("Removal network");
            var managerObject = new GameObject("Removal manager");
            var inputObject = new GameObject("Removal input identity");
            var stateObject = new GameObject("Removal state-only identity");
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                PredictedIdentity input = deterministic
                    ? inputObject.AddComponent<DeterministicInputProbe>()
                    : inputObject.AddComponent<StatefulInputProbe>();
                var state = stateObject.AddComponent<NoInputHistoryProbe>();
                manager.RegisterInstance(inputObject, new PredictedObjectID(720), null, false, false);
                manager.RegisterInstance(stateObject, new PredictedObjectID(721), null, false, false);
                Assert.That(manager.inputHistorySystems, Is.EqualTo(1));
                SetLocalTick(manager, 19);
                SeedInput(input, 19, 190);
                typeof(PredictionManager).GetMethod("CaptureInputHistory", Members)
                    .Invoke(manager, new object[] { 19UL });
                typeof(PredictionManager).GetMethod("CaptureLifecycleHistory", Members)
                    .Invoke(manager, new object[] { 19UL });

                var clientFrames = GetField<List<PlayerPacker>>(
                    typeof(PredictionManager), manager, "_clientFrames");
                clientFrames.Add(new PlayerPacker
                {
                    player = new PlayerID(10, false),
                    lastFullFrameSentTick = 18,
                    lastSentFrameTick = 18,
                    packer = BitPackerPool.Get()
                });
                clientFrames.Add(new PlayerPacker
                {
                    player = new PlayerID(11, false),
                    lastFullFrameSentTick = 18,
                    lastSentFrameTick = 18,
                    packer = BitPackerPool.Get()
                });
                var queues = GetField<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager), manager, "_clientTicks");
                foreach (var clientFrame in clientFrames)
                    queues.Add(clientFrame.player, new PredictionManager.InputQueue { ackedServerTick = 18 });

                manager.UnregisterInstance(state);
                foreach (var clientFrame in clientFrames)
                    Assert.That(clientFrame.requiresFullCheckpoint, Is.False,
                        "a state-only removal does not destroy authoritative input history");

                manager.UnregisterInstance(input);
                Assert.That(manager.inputHistorySystems, Is.Zero);
                foreach (var clientFrame in clientFrames)
                {
                    Assert.That(clientFrame.requiresFullCheckpoint, Is.False,
                        "the removed identity's serialized input still belongs to the retained tick");
                    Assert.That(clientFrame.fullFrame, Is.False,
                        "the checkpoint is selected when the next frame is prepared");
                }

                SetLocalTick(manager, 20);
                typeof(PredictionManager).GetMethod("CaptureInputHistory", Members)
                    .Invoke(manager, new object[] { 20UL });
                typeof(PredictionManager).GetMethod("CaptureLifecycleHistory", Members)
                    .Invoke(manager, new object[] { 20UL });
                var prepare = typeof(PredictionManager).GetMethod("WriteInitialFrameToOthers", Members);
                Assert.That(prepare, Is.Not.Null);
                prepare.Invoke(manager, null);
                foreach (var clientFrame in clientFrames)
                {
                    Assert.That(clientFrame.fullFrame, Is.False);
                    Assert.That(clientFrame.preparedFrameTick, Is.EqualTo(20));
                    Assert.That(clientFrame.preparedBaselineTick, Is.EqualTo(18));
                }
            }
            finally
            {
                Object.DestroyImmediate(stateObject);
                Object.DestroyImmediate(inputObject);
                InputTranscriptFixture.DisposeManagerCaches(managerObject.GetComponent<PredictionManager>());
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        private static PredictionManager CreateSpawnedPredictionManager(
            GameObject managerObject,
            NetworkManager networkManager)
        {
            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(
                typeof(NetworkIdentity),
                manager,
                "<networkManager>k__BackingField",
                networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static void SeedInput(PredictedIdentity identity, ulong tick, int value)
        {
            using var payload = BitPackerPool.Get();
            Packer<bool>.Write(payload, true);
            Packer<TrackedInput>.Write(payload, new TrackedInput(value));
            payload.ResetPositionAndMode(true);
            identity.ReadFirstInput(tick, payload);
        }

        private static void WriteInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong baselineTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteVisibilityInputHistory", Members);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                manager,
                new object[]
                {
                    default(PlayerID),
                    frame,
                    baselineTick,
                    new PlayerVisibilityTimeline()
                });
        }

        private static TrackedInput ReadLengthPrefixedInput(BitPacker frame)
        {
            PackedUInt declaredBits = default;
            Packer<PackedUInt>.Read(frame, ref declaredBits);
            var origin = frame.positionInBits;
            Assert.That(Packer<bool>.Read(frame), Is.True);
            TrackedInput input = default;
            Packer<TrackedInput>.Read(frame, ref input);
            Assert.That(frame.positionInBits - origin, Is.EqualTo((int)declaredBits.value));
            return input;
        }

        private static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, Members);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }

        private static void SetField(
            Type declaringType,
            object target,
            string fieldName,
            object value)
        {
            var field = declaringType.GetField(fieldName, Members);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }

    public sealed class NoInputHistoryProbe : PredictedIdentity<EmptyState>
    {
        protected override void Simulate(ref EmptyState state, float delta) { }
    }
}
