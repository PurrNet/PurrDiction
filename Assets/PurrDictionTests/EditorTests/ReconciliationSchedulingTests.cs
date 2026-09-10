using System;
using System.Collections;
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
    public sealed class ReconciliationSchedulingTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(SchedulingProbeState));
            Packer<SchedulingProbeState>.RegisterWriter(
                (packer, state) => Packer<int>.Write(packer, state.value));
            Packer<SchedulingProbeState>.RegisterReader(
                (BitPacker packer, ref SchedulingProbeState state) =>
                    state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void TickCatchupDefersEveryVerifiedFrameUntilOneRenderReplay()
        {
            using var fixture = new SchedulingFixture();
            fixture.BeginMeasurement();

            // Transport can deliver between catch-up ticks. Reproduce those deliveries and
            // the resulting post-tick callbacks without a socket or Unity player loop.
            for (ulong tick = 11; tick <= 13; tick++)
            {
                fixture.QueueDelta(tick, (int)tick * 10);
                fixture.SetPredictionHead(tick + 4, 900 + (int)tick);
                fixture.PostTick();
            }

            Assert.That(fixture.manager.isActiveAndEnabled, Is.True);
            Assert.That(fixture.PendingFrames, Is.EqualTo(3));
            Assert.That(fixture.probe.verifiedTicks, Is.Empty,
                "post-tick callbacks must not independently reconcile an active client");
            Assert.That(fixture.probe.replayTicks, Is.Empty);
            Assert.That(fixture.rollbackStarts, Is.Zero);
            Assert.That(fixture.PendingViewValue, Is.EqualTo(913),
                "the most recent forward state must still be captured before correction");

            fixture.RenderUpdate();

            Assert.That(fixture.probe.verifiedTicks, Is.EqualTo(new ulong[] { 11, 12, 13 }));
            Assert.That(fixture.probe.verifiedValues, Is.EqualTo(new[] { 110, 120, 130 }),
                "batching must retain each authoritative state and its verified simulation");
            Assert.That(fixture.probe.replayTicks, Is.EqualTo(new ulong[] { 14, 15, 16 }),
                "only the future of the last verified frame should be replayed");
            Assert.That(fixture.probe.currentState.value, Is.EqualTo(134));
            Assert.That(fixture.PendingViewValue, Is.EqualTo(134),
                "render reconciliation must refresh the pending forward sample");
            Assert.That(fixture.PendingFrames, Is.Zero);
            Assert.That(fixture.rollbackStarts, Is.EqualTo(1));
            Assert.That(fixture.rollbackFinishes, Is.EqualTo(1));
            Assert.That(fixture.manager.renderPhaseFrameAppliesTotal, Is.EqualTo(1));
            Assert.That(fixture.manager.tickPhaseFrameAppliesTotal, Is.Zero);
            Assert.That(Get<ulong>(typeof(PredictionManager), fixture.manager, "_verifiedServerTick"),
                Is.EqualTo(13));
            Assert.That(Get<ulong>(typeof(PredictionManager), fixture.manager, "_ackedServerTick"),
                Is.EqualTo(13));
            Assert.That(fixture.verified.ReadOrPrevious(13, out var authoritative), Is.True);
            Assert.That(authoritative.state.value, Is.EqualTo(130));

            fixture.RenderUpdate();
            Assert.That(fixture.rollbackStarts, Is.EqualTo(1),
                "an empty render drain must not replay the future again");

            var performance = fixture.EndMeasurement();
            Assert.That(performance.correctionBatches, Is.EqualTo(1));
            Assert.That(performance.appliedVerifiedFrames, Is.EqualTo(3));
            Assert.That(performance.verified.count, Is.EqualTo(3));
            Assert.That(performance.speculativeReplay.count, Is.EqualTo(3));
        }

        [Test]
        public void RenderWithoutNewTickAppliesStateWithoutInventingAViewSample()
        {
            using var fixture = new SchedulingFixture();
            fixture.SetPredictionHead(15, 900);
            fixture.QueueDelta(11, 110);
            Assert.That(fixture.PendingViewValue, Is.Null);

            fixture.RenderUpdate();

            Assert.That(fixture.probe.verifiedTicks, Is.EqualTo(new ulong[] { 11 }));
            Assert.That(fixture.probe.verifiedValues, Is.EqualTo(new[] { 110 }));
            Assert.That(fixture.probe.replayTicks, Is.EqualTo(new ulong[] { 12, 13, 14 }));
            Assert.That(fixture.probe.currentState.value, Is.EqualTo(114));
            Assert.That(fixture.PendingFrames, Is.Zero);
            Assert.That(fixture.PendingViewValue, Is.Null,
                "a render-only correction must not add a tick sample to interpolation");
            Assert.That(fixture.manager.renderPhaseFrameAppliesTotal, Is.EqualTo(1));
            Assert.That(fixture.rollbackStarts, Is.EqualTo(1));
            Assert.That(fixture.rollbackFinishes, Is.EqualTo(1));
        }

        [Test]
        public void DisabledManagerRetainsPostTickReconciliationFallback()
        {
            using var fixture = new SchedulingFixture();
            fixture.SetPredictionHead(15, 900);
            fixture.manager.enabled = false;
            fixture.QueueDelta(11, 110);

            // A disabled component still has its subscribed tick callback, but Unity no
            // longer calls Update. Its fallback must consume the queue here.
            fixture.PostTick();

            Assert.That(fixture.probe.verifiedTicks, Is.EqualTo(new ulong[] { 11 }));
            Assert.That(fixture.probe.verifiedValues, Is.EqualTo(new[] { 110 }));
            Assert.That(fixture.probe.replayTicks, Is.EqualTo(new ulong[] { 12, 13, 14 }));
            Assert.That(fixture.probe.currentState.value, Is.EqualTo(114));
            Assert.That(fixture.PendingFrames, Is.Zero);
            Assert.That(fixture.PendingViewValue, Is.EqualTo(114));
            Assert.That(fixture.manager.tickPhaseFrameAppliesTotal, Is.EqualTo(1));
            Assert.That(fixture.manager.renderPhaseFrameAppliesTotal, Is.Zero);
            Assert.That(fixture.rollbackStarts, Is.EqualTo(1));
            Assert.That(fixture.rollbackFinishes, Is.EqualTo(1));
        }

        [Test]
        public void RenderAtStartupLeavesFramesForTheInitialTick()
        {
            using var fixture = new SchedulingFixture();
            fixture.SetPredictionHead(1, 900);
            fixture.QueueDelta(11, 110);

            fixture.RenderUpdate();

            Assert.That(fixture.PendingFrames, Is.EqualTo(1));
            Assert.That(fixture.probe.verifiedTicks, Is.Empty);
            Assert.That(fixture.probe.replayTicks, Is.Empty);
            Assert.That(fixture.rollbackStarts, Is.Zero);
            Assert.That(fixture.PendingViewValue, Is.Null);
        }

        private sealed class SchedulingFixture : IDisposable
        {
            private readonly GameObject _networkObject = new("Scheduling test network");
            private readonly GameObject _managerObject = new("Scheduling test receiver");
            private readonly GameObject _senderManagerObject = new("Scheduling test sender");
            private readonly GameObject _probeObject = new("Scheduling test receiver state");
            private readonly GameObject _senderObject = new("Scheduling test sender state");
            private readonly NetworkManager _network;
            private readonly PredictionManager _senderManager;
            private readonly SchedulingProbeIdentity _sender;
            private readonly double _previousCadence;
            private bool _measurementActive;

            public readonly PredictionManager manager;
            public readonly SchedulingProbeIdentity probe;
            public readonly History<FULL_STATE<SchedulingProbeState>> verified;
            public int rollbackStarts;
            public int rollbackFinishes;

            public SchedulingFixture()
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = _networkObject.AddComponent<NetworkManager>();
                manager = CreateManager(_managerObject);
                _senderManager = CreateManager(_senderManagerObject);

                // Exercise the real client gates, while omitting transport registration and
                // scene bootstrap. Neither path under test needs an active socket.
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", true);
                Set(typeof(PredictionManager), manager, "_verifiedServerTick", 10UL);
                Set(typeof(PredictionManager), manager, "_latestFrameServerTick", 10UL);

                var id = new PredictedComponentID(new PredictedObjectID(701), 0);
                probe = _probeObject.AddComponent<SchedulingProbeIdentity>();
                verified = InitializeIdentity(probe, manager, id, 900);
                _sender = _senderObject.AddComponent<SchedulingProbeIdentity>();
                InitializeIdentity(_sender, _senderManager, id, 100);
                manager.onStartingToRollback += () => rollbackStarts++;
                manager.onRollbackFinished += () => rollbackFinishes++;
                SetPredictionHead(15, 900);
            }

            public int PendingFrames => ((ICollection)Get<object>(
                typeof(PredictionManager), manager, "_deltas")).Count;

            public int? PendingViewValue => Get<FULL_STATE<SchedulingProbeState>?>(
                typeof(PredictedIdentity<SchedulingProbeState>), probe, "_viewState")?.state.value;

            public void SetPredictionHead(ulong tick, int value)
            {
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
                Set(typeof(PredictionManager), manager, "<localTickInContext>k__BackingField", tick);
                probe.currentState.value = value;
            }

            public void PostTick() => Invoke(manager, "OnPostTick");
            public void RenderUpdate() => Invoke(manager, "Update");

            public void BeginMeasurement()
            {
                PredictionPerformanceTelemetry.Begin(manager);
                _measurementActive = true;
            }

            public PredictionPerformanceSnapshot EndMeasurement()
            {
                var snapshot = PredictionPerformanceTelemetry.End();
                _measurementActive = false;
                return snapshot;
            }

            public void QueueDelta(ulong tick, int value)
            {
                Set(typeof(PredictionManager), _senderManager, "<localTick>k__BackingField", tick);
                _sender.fullPredictedState = FullState(value);
                var frame = BitPackerPool.Get();
                try
                {
                    Packer<PackedUInt>.Write(frame, 0u); // visibility deletions
                    Packer<bool>.Write(frame, false); // hierarchy record
                    Packer<PackedUInt>.Write(frame, 0u); // guaranteed input history
                    Packer<PackedUInt>.Write(frame, 0u); // newest inputs
                    using var payload = BitPackerPool.Get();
                    Assert.That(_sender.RunWriteCurrentState(default, payload, 10), Is.True);
                    AddressedPredictionRecords.WriteSectionCount(1, frame);
                    AddressedPredictionRecords.WriteRecord(frame, probe.id, false, payload);
                    AddressedPredictionRecords.WriteSectionCount(0, frame); // event handlers
                    int length = frame.positionInBytes;
                    frame.ResetPositionAndMode(true);
                    Invoke(manager, "HandleFrameFromServer", tick, 10UL, tick, false,
                        false, default(PackedInt), false, default(PackedInt),
                        new BitPackerWithLength(length, frame));
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
                // The real receive handler transfers ownership to the manager's queue.
                // Use deltas: full snapshots intentionally supersede older queued frames.
            }

            public void Dispose()
            {
                if (_measurementActive)
                    EndMeasurement();
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
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

        private static PredictionManager CreateManager(GameObject gameObject)
        {
            var manager = gameObject.AddComponent<PredictionManager>();
            Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            Set(typeof(PredictionManager), manager, "<tickDelta>k__BackingField", 1f / 20);
            // Leave the refreshed latch pending so it can be inspected after Update.
            Set(typeof(PredictionManager), manager, "_updateViewMode", UpdateViewMode.None);
            return manager;
        }

        private static History<FULL_STATE<SchedulingProbeState>> InitializeIdentity(
            SchedulingProbeIdentity identity, PredictionManager manager,
            PredictedComponentID id, int initialValue)
        {
            identity.AttachForTest(manager, id);
            identity.fullPredictedState = FullState(initialValue);
            var predicted = new History<FULL_STATE<SchedulingProbeState>>(200);
            predicted.Write(0, FullState(initialValue));
            Set(typeof(PredictedIdentity<SchedulingProbeState>), identity, "_stateHistory", predicted);
            var verified = manager.GetVerifiedHistory<FULL_STATE<SchedulingProbeState>>(id, out _);
            verified.Write(10, FullState(100));
            Set(typeof(PredictedIdentity<SchedulingProbeState>), identity, "_verifiedHistory", verified);
            identity.lastVerifiedTick = 10;
            Get<List<PredictedIdentity>>(typeof(PredictionManager), manager, "_systems").Add(identity);
            Set(typeof(PredictionManager), manager, "_systemsCount", 1);
            Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                typeof(PredictionManager), manager, "_instanceMap").Add(id, identity);
            return verified;
        }

        private static FULL_STATE<SchedulingProbeState> FullState(int value)
        {
            var state = new FULL_STATE<SchedulingProbeState>
            {
                state = new SchedulingProbeState { value = value }
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

    public struct SchedulingProbeState : IPredictedData<SchedulingProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class SchedulingProbeIdentity : PredictedIdentity<SchedulingProbeState>
    {
        public readonly List<ulong> verifiedTicks = new();
        public readonly List<int> verifiedValues = new();
        public readonly List<ulong> replayTicks = new();

        public void AttachForTest(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
            myType = GetType();
        }

        protected override void Simulate(ref SchedulingProbeState state, float delta)
        {
            if (predictionManager.isVerified)
            {
                verifiedTicks.Add(predictionManager.localTickInContext);
                verifiedValues.Add(state.value);
            }
            else if (predictionManager.isReplaying)
                replayTicks.Add(predictionManager.localTickInContext);
            state.value++;
        }

        protected override void WriteDeltaState(BitPacker packer,
            in SchedulingProbeState baseline, in SchedulingProbeState current)
        {
            Packer<int>.Write(packer, current.value);
        }

        protected override void ReadDeltaState(BitPacker packer,
            in SchedulingProbeState baseline, ref SchedulingProbeState state)
        {
            state.value = Packer<int>.Read(packer);
        }
    }
}
