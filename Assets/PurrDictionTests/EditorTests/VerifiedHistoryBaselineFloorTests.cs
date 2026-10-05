using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Artifact-only candidate regressions. The wire fixture uses the real receive and
    // render-drain paths; its delta codec requires the correct historical baseline.
    public sealed class VerifiedHistoryBaselineFloorTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(BaselineFloorProbeState));
            Packer<BaselineFloorProbeState>.RegisterWriter(
                (packer, state) => Packer<int>.Write(packer, state.value));
            Packer<BaselineFloorProbeState>.RegisterReader(
                (BitPacker packer, ref BaselineFloorProbeState state) =>
                    state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void SuccessfulDeltaAdvancesOnlyAfterVerifiedSimulationAndUsesBaselineNotServerTick()
        {
            using var f = new Fixture();
            var floorsDuringVerifiedPhysics = new List<ulong>();
            f.manager.onAfterPhysicsPass += () =>
            {
                if (f.manager.isVerified)
                    floorsDuringVerifiedPhysics.Add(f.Floor);
            };
            f.Queue(11, 8, 110);
            Assert.That(f.Floor, Is.Zero, "receiving a frame does not establish an applied floor");
            f.Render();

            Assert.That(floorsDuringVerifiedPhysics, Is.EqualTo(new ulong[] { 0 }));
            Assert.That(f.Floor, Is.EqualTo(8));
            Assert.That(f.Ack, Is.EqualTo(11));
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 110 }));
            Assert.That(f.verified.Read(11, out var first), Is.True);
            Assert.That(first.state.value, Is.EqualTo(110));

            // Before the server receives that ACK it can send another frame using 8.
            // Pruning to serverTick=11 would silently corrupt this additive delta.
            Assert.That(f.verified.PruneBeforeBaseline(f.Floor), Is.EqualTo(1));
            f.Queue(12, 8, 120);
            f.Render();
            Assert.That(f.Floor, Is.EqualTo(8));
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 110, 120 }));
            Assert.That(f.verified.Read(12, out var second), Is.True);
            Assert.That(second.state.value, Is.EqualTo(120));
        }

        [Test]
        public void LaterSuccessfulBaselineCanAdvanceFloorAcrossGapCatchup()
        {
            using var f = new Fixture();
            f.Queue(11, 8, 110);
            f.Render();
            var floorsDuringVerifiedPhysics = new List<ulong>();
            f.manager.onAfterPhysicsPass += () =>
            {
                if (f.manager.isVerified)
                    floorsDuringVerifiedPhysics.Add(f.Floor);
            };
            f.SetPredictionHead(18);
            f.Queue(13, 10, 130);
            f.Render();
            Assert.That(floorsDuringVerifiedPhysics, Is.Not.Empty);
            Assert.That(floorsDuringVerifiedPhysics, Is.All.EqualTo(8UL),
                "gap reconstruction and the final verified pass must retain the old floor");
            Assert.That(f.Floor, Is.EqualTo(10));
            Assert.That(f.verified.Read(13, out var state), Is.True);
            Assert.That(state.state.value, Is.EqualTo(130));
        }

        [Test]
        public void FullSnapshotAppliesWithoutEstablishingOrAdvancingDeltaFloor()
        {
            using var f = new Fixture();
            f.Floor = 8;
            f.Queue(11, 999, 777, fullFrame: true);
            f.Render();
            Assert.That(f.Floor, Is.EqualTo(8));
            Assert.That(f.Ack, Is.EqualTo(11));
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 777 }));
            Assert.That(f.verified.Read(11, out var state), Is.True);
            Assert.That(state.state.value, Is.EqualTo(777));
        }

        [Test]
        public void StaleFrameNeverAdvancesFloorOrInvokesItsDecoder()
        {
            using var f = new Fixture();
            f.Queue(11, 8, 110);
            f.Render();
            int reads = f.probe.deltaReads;
            // A deliberately unusable baseline detects any update before the stale guard.
            f.Queue(10, 999, 1000);
            f.Render();
            Assert.That(f.Floor, Is.EqualTo(8));
            Assert.That(f.Ack, Is.EqualTo(11));
            Assert.That(f.probe.deltaReads, Is.EqualTo(reads));
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 110 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedAddressedRecordDoesNotAdvanceFloorIncludingQuarantinedRoster(bool rosterMismatch)
        {
            using var f = new Fixture();
            f.Floor = 2;
            f.probe.readFailure = rosterMismatch
                ? new PredictedModuleRosterMismatchException("baseline floor injected roster mismatch")
                : new InvalidOperationException("baseline floor injected decode failure");
            LogAssert.Expect(LogType.Error, new Regex(rosterMismatch
                ? "baseline floor injected roster mismatch.*State replication"
                : "Discarded prediction record.*baseline floor injected decode failure"));
            f.Queue(11, 8, 110);
            f.Render();

            Assert.That(f.probe.deltaReads, Is.EqualTo(1));
            Assert.That(f.Floor, Is.EqualTo(2));
            Assert.That(f.verified.Read(11, out _), Is.False);
            Assert.That(f.Ack, Is.EqualTo(rosterMismatch ? 11UL : 0UL),
                "the new floor guard must preserve existing quarantine versus ordinary-failure ACK behavior");
        }

        [Test]
        public void CleanupStartsNewEpochWithNoOldFloorOrSharedStore()
        {
            using var f = new Fixture();
            f.Queue(11, 8, 110);
            f.Render();
            var previous = f.verified;
            // Avoid scheduling Unity Destroy in an EditMode test; the actual manager
            // cleanup still resets every transport/epoch/store field under test.
            f.DetachProbe();
            Invoke(f.manager, "CleanupAllSystems");
            Assert.That(f.Floor, Is.Zero);
            Assert.That(Get<bool>(typeof(PredictionManager), f.manager, "_frameApplyHadBaselineFailure"), Is.False);
            Assert.That(f.Ack, Is.Zero);
            Assert.That(Get<ulong>(typeof(PredictionManager), f.manager, "_verifiedServerTick"), Is.Zero);
            Assert.That(previous.Count, Is.Zero);
            var next = f.manager.GetVerifiedHistory<FULL_STATE<BaselineFloorProbeState>>(f.probe.id, out bool created);
            Assert.That(created, Is.True);
            Assert.That(next, Is.Not.SameAs(previous));
            Assert.That(next.Count, Is.Zero);
        }

        [TestCase(8UL, 8UL, 2)]
        [TestCase(9UL, 8UL, 2)]
        [TestCase(1UL, 2UL, 0)]
        [TestCase(99UL, 14UL, 4)]
        public void PruningKeepsExactOrPreviousAnchorAndEveryLaterEntry(
            ulong floor, ulong oldestExpected, int removedExpected)
        {
            var history = new History<BaselineFloorProbeState>(100);
            try
            {
                foreach (ulong tick in new ulong[] { 2, 5, 8, 11, 14 })
                    history.Write(tick, new BaselineFloorProbeState { value = (int)tick * 10 });
                var expected = new Dictionary<ulong, int?>();
                for (ulong tick = floor; tick <= Math.Max(floor, 20UL); tick++)
                    expected.Add(tick, history.ReadOrPrevious(tick, out var value) ? value.value : null);

                Assert.That(history.PruneBeforeBaseline(floor), Is.EqualTo(removedExpected));
                Assert.That(history.OldestTick, Is.EqualTo(oldestExpected));
                foreach (var pair in expected)
                {
                    bool found = history.ReadOrPrevious(pair.Key, out var actual);
                    Assert.That(found, Is.EqualTo(pair.Value.HasValue), $"lookup at {pair.Key}");
                    if (found)
                        Assert.That(actual.value, Is.EqualTo(pair.Value.Value), $"lookup at {pair.Key}");
                }
            }
            finally { history.Clear(); }
        }

        [Test]
        public void PartialRemovalBudgetNeverRemovesRequiredAnchor()
        {
            var history = new History<BaselineFloorProbeState>(100);
            try
            {
                for (ulong tick = 1; tick <= 6; tick++)
                    history.Write(tick, new BaselineFloorProbeState { value = (int)tick });
                Assert.That(history.PruneBeforeBaseline(5, 0), Is.Zero);
                Assert.That(history.PruneBeforeBaseline(5, 2), Is.EqualTo(2));
                Assert.That(history.OldestTick, Is.EqualTo(3));
                Assert.That(history.Read(5, out var anchor), Is.True);
                Assert.That(anchor.value, Is.EqualTo(5));
                Assert.That(history.PruneBeforeBaseline(5, 2), Is.EqualTo(2));
                Assert.That(history.OldestTick, Is.EqualTo(5));
                Assert.That(history.PruneBeforeBaseline(5, 2), Is.Zero);
                Assert.That(history.Count, Is.EqualTo(2));
                Assert.That(history.Read(6, out var later), Is.True);
                Assert.That(later.value, Is.EqualTo(6));
            }
            finally { history.Clear(); }
        }

        [Test]
        public void OmittedUnchangedRecordUsesNonExactFloorAnchorRatherThanNewerUnackedState()
        {
            using var f = new Fixture();
            f.Floor = 9;
            Assert.That(f.verified.PruneBeforeBaseline(f.Floor), Is.EqualTo(1));
            f.Queue(11, 9, 80, omitUnchanged: true);
            f.Render();
            Assert.That(f.Floor, Is.EqualTo(9));
            Assert.That(f.probe.deltaReads, Is.Zero, "omission must use the normal carry-forward path");
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 80 }));
            Assert.That(f.verified.Read(11, out var state), Is.True);
            Assert.That(state.state.value, Is.EqualTo(80));
        }

        [Test]
        public void OrphanMaintenanceRetainsLogicalStoreForFullEntryRebindAndSubsequentDelta()
        {
            using var f = new Fixture();
            f.Floor = 9;
            var original = f.verified;
            f.DetachProbe();
            Invoke(f.manager, "MaintainVerifiedStoreStorage");
            Assert.That(original.OldestTick, Is.EqualTo(8));
            f.RebindProbe();
            Assert.That(f.verified, Is.SameAs(original), "absence must not retire the shared logical key");

            // Visibility reentry carries a full identity record inside a delta frame.
            // Exercise that wire contract, without claiming to test visibility selection.
            f.Queue(11, 9, 777, fullEntry: true);
            f.Render();
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 777 }));
            f.Queue(12, 11, 790);
            f.Render();
            Assert.That(f.Floor, Is.EqualTo(11));
            Assert.That(f.probe.verifiedValues, Is.EqualTo(new[] { 777, 790 }));
            Assert.That(original.Read(12, out var state), Is.True);
            Assert.That(state.state.value, Is.EqualTo(790));
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _networkObject = new("Baseline floor test network");
            private readonly GameObject _managerObject = new("Baseline floor test receiver");
            private readonly GameObject _senderManagerObject = new("Baseline floor test sender");
            private readonly GameObject _probeObject = new("Baseline floor receiver state");
            private readonly GameObject _senderObject = new("Baseline floor sender state");
            private readonly NetworkManager _network;
            private readonly PredictionManager _senderManager;
            private readonly BaselineFloorProbeIdentity _sender;
            public readonly PredictionManager manager;
            public readonly BaselineFloorProbeIdentity probe;
            public History<FULL_STATE<BaselineFloorProbeState>> verified;

            public Fixture()
            {
                _network = _networkObject.AddComponent<NetworkManager>();
                manager = CreateManager(_managerObject);
                _senderManager = CreateManager(_senderManagerObject);
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", true);
                Set(typeof(PredictionManager), manager, "_verifiedServerTick", 10UL);
                Set(typeof(PredictionManager), manager, "_awaitingFullFrame", false);
                Set(typeof(PredictionManager), manager, "_latestFrameServerTick", 10UL);
                var id = new PredictedComponentID(new PredictedObjectID(1701), 0);
                probe = _probeObject.AddComponent<BaselineFloorProbeIdentity>();
                verified = InitializeIdentity(probe, manager, id);
                _sender = _senderObject.AddComponent<BaselineFloorProbeIdentity>();
                InitializeIdentity(_sender, _senderManager, id);
                SetPredictionHead(16);
            }

            public ulong Floor
            {
                get => Get<ulong>(typeof(PredictionManager), manager, "_verifiedHistoryBaselineFloor");
                set => Set(typeof(PredictionManager), manager, "_verifiedHistoryBaselineFloor", value);
            }
            public ulong Ack => Get<ulong>(typeof(PredictionManager), manager, "_ackedServerTick");
            public void Render() => Invoke(manager, "Update");
            public void SetPredictionHead(ulong tick)
            {
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
                Set(typeof(PredictionManager), manager, "<localTickInContext>k__BackingField", tick);
            }

            public void DetachProbe()
            {
                Get<List<PredictedIdentity>>(typeof(PredictionManager), manager, "_systems").Clear();
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager), manager, "_instanceMap").Clear();
                Set(typeof(PredictionManager), manager, "_systemsCount", 0);
            }

            public void RebindProbe()
            {
                verified = manager.GetVerifiedHistory<FULL_STATE<BaselineFloorProbeState>>(probe.id, out bool created);
                Assert.That(created, Is.False);
                Set(typeof(PredictedIdentity<BaselineFloorProbeState>), probe, "_verifiedHistory", verified);
                Register(probe, manager);
            }

            public void Queue(ulong tick, ulong baseline, int value, bool fullFrame = false,
                bool fullEntry = false, bool omitUnchanged = false)
            {
                Set(typeof(PredictionManager), _senderManager, "<localTick>k__BackingField", tick);
                _sender.fullPredictedState = FullState(value);
                var frame = BitPackerPool.Get();
                try
                {
                    if (fullFrame)
                    {
                        Packer<PackedInt>.Write(frame, 20);
                        Packer<float>.Write(frame, 1f / 20);
                        Packer<uint>.Write(frame, 123u);
                    }
                    // The stale-frame test intentionally supplies a future baseline;
                    // its receive guard must discard that packet before parsing it.
                    uint inputTicks = !fullFrame && baseline < tick ? checked((uint)(tick - baseline)) : 0u;
                    Packer<PackedUInt>.Write(frame, 0u); // visibility deletions
                    Packer<bool>.Write(frame, false); // hierarchy record
                    if (!fullFrame)
                    {
                        Packer<PackedUInt>.Write(frame, inputTicks);
                        for (uint inputTick = 0; inputTick < inputTicks; inputTick++)
                            Packer<PackedUInt>.Write(frame, 0u); // complete empty transcript
                        Packer<bool>.Write(frame, false); // no historical lifecycle hierarchy
                    }
                    using var payload = BitPackerPool.Get();
                    if (fullFrame || fullEntry)
                    {
                        _sender.RunWriteFirstState(tick, payload);
                    }
                    else
                    {
                        Packer<bool>.Write(payload, false); // no entering baseline override
                        bool changed = _sender.RunWriteCurrentState(default, payload, baseline);
                        if (omitUnchanged)
                            Assert.That(changed, Is.False, "the omitted payload must actually be unchanged");
                    }
                    AddressedPredictionRecords.WriteSectionCount(omitUnchanged ? 0 : 1, frame);
                    if (!omitUnchanged)
                        AddressedPredictionRecords.WriteRecord(frame, probe.id, fullFrame || fullEntry, payload);
                    if (fullFrame)
                        AddressedPredictionRecords.WriteSectionCount(0, frame); // first inputs
                    Packer<PackedUInt>.Write(frame, 0u); // historical physics event batches
                    AddressedPredictionRecords.WriteSectionCount(0, frame); // event handlers
                    int length = frame.positionInBytes;
                    frame.ResetPositionAndMode(true);
                    Invoke(manager, "HandleFrameFromServer", tick, baseline, tick, fullFrame,
                        false, default(PackedInt), false, default(PackedInt),
                        new BitPackerWithLength(length, frame));
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                var queue = Get<object>(typeof(PredictionManager), manager, "_deltas");
                foreach (IDisposable frame in (IEnumerable)queue)
                    frame.Dispose();
                queue.GetType().GetMethod("Clear").Invoke(queue, null);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", false);
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                Object.DestroyImmediate(_probeObject);
                Object.DestroyImmediate(_senderObject);
                Object.DestroyImmediate(_managerObject);
                Object.DestroyImmediate(_senderManagerObject);
                Object.DestroyImmediate(_networkObject);
            }
        }

        private static PredictionManager CreateManager(GameObject go)
        {
            var manager = go.AddComponent<PredictionManager>();
            Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            Set(typeof(PredictionManager), manager, "<tickDelta>k__BackingField", 1f / 20);
            Set(typeof(PredictionManager), manager, "_updateViewMode", UpdateViewMode.None);
            return manager;
        }

        private static History<FULL_STATE<BaselineFloorProbeState>> InitializeIdentity(
            BaselineFloorProbeIdentity identity, PredictionManager manager, PredictedComponentID id)
        {
            identity.Attach(manager, id);
            identity.fullPredictedState = FullState(900);
            var predicted = new History<FULL_STATE<BaselineFloorProbeState>>(200);
            predicted.Write(0, FullState(900));
            Set(typeof(PredictedIdentity<BaselineFloorProbeState>), identity, "_stateHistory", predicted);
            var verified = manager.GetVerifiedHistory<FULL_STATE<BaselineFloorProbeState>>(id, out _);
            foreach (ulong tick in new ulong[] { 2, 8, 10 })
                verified.Write(tick, FullState((int)tick * 10));
            Set(typeof(PredictedIdentity<BaselineFloorProbeState>), identity, "_verifiedHistory", verified);
            identity.lastVerifiedTick = 10;
            Register(identity, manager);
            return verified;
        }

        private static void Register(BaselineFloorProbeIdentity identity, PredictionManager manager)
        {
            var systems = Get<List<PredictedIdentity>>(typeof(PredictionManager), manager, "_systems");
            systems.Add(identity);
            Set(typeof(PredictionManager), manager, "_systemsCount", systems.Count);
            Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                typeof(PredictionManager), manager, "_instanceMap").Add(identity.id, identity);
        }

        private static FULL_STATE<BaselineFloorProbeState> FullState(int value)
        {
            var state = new FULL_STATE<BaselineFloorProbeState>
            {
                state = new BaselineFloorProbeState { value = value }
            };
            state.prediction.wasOnSimulationStartCalled = true;
            return state;
        }

        private static T Get<T>(Type type, object target, string name)
        {
            var field = type.GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return (T)field.GetValue(target);
        }
        private static void Set(Type type, object target, string name, object value)
        {
            var field = type.GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            field.SetValue(target, value);
        }
        private static void Invoke(PredictionManager manager, string name, params object[] arguments)
        {
            var method = typeof(PredictionManager).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            method.Invoke(manager, arguments);
        }
    }

    public struct BaselineFloorProbeState : IPredictedData<BaselineFloorProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class BaselineFloorProbeIdentity : PredictedIdentity<BaselineFloorProbeState>
    {
        public readonly List<int> verifiedValues = new();
        public Exception readFailure;
        public int deltaReads;
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
            myType = GetType();
        }
        protected override void Simulate(ref BaselineFloorProbeState state, float delta)
        {
            if (predictionManager.isVerified)
                verifiedValues.Add(state.value);
            state.value++;
        }
        protected override void WriteDeltaState(BitPacker packer,
            in BaselineFloorProbeState baseline, in BaselineFloorProbeState current)
            => Packer<int>.Write(packer, current.value - baseline.value);
        protected override void ReadDeltaState(BitPacker packer,
            in BaselineFloorProbeState baseline, ref BaselineFloorProbeState state)
        {
            deltaReads++;
            if (readFailure != null)
                throw readFailure;
            state.value = baseline.value + Packer<int>.Read(packer);
        }
    }
}
