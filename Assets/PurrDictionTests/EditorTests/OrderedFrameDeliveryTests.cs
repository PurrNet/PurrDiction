using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Real frame writers, codecs, receive queue, verified replay and input ACK handler. Every frame
    // rides one ordered reliable stream, so the fixture hands each prepared frame to the receiver in
    // send order; these tests do not invoke generated RPC senders or claim socket coverage.
    public sealed class OrderedFrameDeliveryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(FrameProbeState));
            Packer<FrameProbeState>.RegisterWriter((packer, state) => Packer<int>.Write(packer, state.value));
            Packer<FrameProbeState>.RegisterReader((BitPacker packer, ref FrameProbeState state) =>
                state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void DeltasApplyInOrderEachAgainstThePreviousFrame()
        {
            using var f = new Fixture();
            using var first = f.Prepare(11, 110);
            using var second = f.Prepare(12, 120);
            Assert.That(first.baseline, Is.EqualTo(10), "the previous frame on the stream is the baseline");
            Assert.That(second.baseline, Is.EqualTo(11), "no acknowledgement is awaited before the next delta");

            f.Receive(first);
            f.Receive(second);
            f.Drain();

            Assert.That(f.ClientVerified, Is.EqualTo(12));
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.Verified(12).state.value, Is.EqualTo(120));
            Assert.That(f.probe.deltaReads, Is.EqualTo(2));
            Assert.That(f.probe.lastInput, Is.EqualTo(120));
        }

        [Test]
        public void FullFrameSupersedesDeltasStillQueuedBeforeIt()
        {
            using var f = new Fixture();
            using var stale = f.Prepare(11, 110);
            using var full = f.Prepare(12, 120, full: true);
            using var next = f.Prepare(13, 130);
            Assert.That(full.full, Is.True);
            Assert.That(next.baseline, Is.EqualTo(12));

            f.Receive(stale);
            f.Receive(full);
            f.Receive(next);
            f.Drain();

            Assert.That(f.ClientVerified, Is.EqualTo(13));
            Assert.That(f.Verified(13).state.value, Is.EqualTo(130));
            Assert.That(f.probe.deltaReads, Is.EqualTo(1), "the delta queued before the full was discarded unread");
        }

        [Test]
        public void FailedDeltaDropsLaterDeltasUntilTheNextFull()
        {
            using var f = new Fixture();
            using var good = f.Prepare(11, 110);
            using var failing = f.Prepare(12, 120);
            using var dropped = f.Prepare(13, 130);
            using var full = f.Prepare(14, 140, full: true);
            using var resumed = f.Prepare(15, 150);

            f.Receive(good);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(11));

            f.probe.failSaveTick = 13;
            LogAssert.Expect(LogType.Error, new Regex(
                @"Cannot apply prediction frame 12: System\.InvalidOperationException: checkpoint test entering state failure"));
            f.Receive(failing);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(11));
            Assert.That(f.AwaitingFull, Is.True);
            Assert.That(f.ResyncPending, Is.True);
            int reads = f.probe.deltaReads;

            f.probe.failSaveTick = 0;
            f.Receive(dropped);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(11), "a delta cannot continue a frame that failed");
            Assert.That(f.probe.deltaReads, Is.EqualTo(reads));

            f.Receive(full);
            f.Receive(resumed);
            f.Drain();
            Assert.That(f.AwaitingFull, Is.False);
            Assert.That(f.ResyncPending, Is.False);
            Assert.That(f.ClientVerified, Is.EqualTo(15));
            Assert.That(f.Verified(15).state.value, Is.EqualTo(150));
        }

        [Test]
        public void FailedFullWaitsForTheNextFull()
        {
            using var f = new Fixture();
            using var failing = f.Prepare(11, 110, full: true);
            using var dropped = f.Prepare(12, 120);
            using var full = f.Prepare(13, 130, full: true);

            f.probe.failFull = true;
            LogAssert.Expect(LogType.Error, new Regex("Discarded prediction record.*checkpoint test full decode failure"));
            f.Receive(failing);
            f.Receive(dropped);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(10), "a partially decoded full is not accepted");
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.AwaitingFull, Is.True);
            Assert.That(f.ResyncPending, Is.True);
            Assert.That(f.probe.deltaReads, Is.Zero);

            f.probe.failFull = false;
            f.Receive(full);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(13));
            Assert.That(f.AwaitingFull, Is.False);
            Assert.That(f.ResyncPending, Is.False);
        }

        [Test]
        public void ResyncRequestIsCoveredByAFullAlreadyOnTheStream()
        {
            using var f = new Fixture();
            f.Request(12);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.True, "no full newer than the failure was sent yet");

            using var full = f.Prepare(13, 130);
            Assert.That(full.full, Is.True);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.False);
            Assert.That(f.ServerFrame.lastFullFrameSentTick, Is.EqualTo(13));

            f.Request(12);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.False,
                "the full at 13 follows the failed tick on the ordered stream");

            f.Request(13);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.False, "requests are served at most once per second");
            f.ClearResyncCooldown();
            f.Request(13);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.True, "a failed full needs a newer one");
        }

        [Test]
        public void AckBeyondTheServerTickIsIgnored()
        {
            using var f = new Fixture();
            using var delta = f.Prepare(12, 120);

            f.DeliverAck(13);
            Assert.That(f.ServerAck, Is.EqualTo(8), "an ACK ahead of the current server tick is ignored");
            f.DeliverAck(12);
            Assert.That(f.ServerAck, Is.EqualTo(12), "the current tick is the newest frame this server could have sent");
        }

        private sealed class Packet : IDisposable
        {
            public readonly ulong tick, baseline;
            public readonly bool full;
            public readonly BitPacker payload;
            public Packet(ulong tick, ulong baseline, bool full, BitPacker source)
            {
                this.tick = tick; this.baseline = baseline; this.full = full;
                payload = Copy(source);
            }
            public void Dispose() => payload.Dispose();
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            private readonly NetworkManager _network;
            private readonly PredictionManager _server;
            private readonly FrameProbeIdentity _sender;
            private readonly History<FULL_STATE<FrameProbeState>> _verified;
            private readonly List<PlayerPacker> _frames;
            private readonly PredictionManager.InputQueue _input;
            private readonly double _previousCadence;
            private ulong _lastPreparedTick = 8;
            private readonly PlayerID _player = default;
            public readonly PredictionManager client;
            public readonly FrameProbeIdentity probe;

            public Fixture()
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = Create<NetworkManager>("Ordered frame client network");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                client = CreateManager("Ordered frame receiver");
                _server = CreateManager("Ordered frame sender");
                Set(typeof(NetworkIdentity), client, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), client, "_isSpawnedClient", false);
                Set(client, "_verifiedServerTick", 10UL);
                Set(client, "_latestFrameServerTick", 10UL);
                Set(client, "_ackedServerTick", 10UL);
                Set(client, "_awaitingFullFrame", false);
                Set(_server, "<cachedIsServer>k__BackingField", true);
                var id = new PredictedComponentID(new PredictedObjectID(2822), 0);
                probe = Create<FrameProbeIdentity>("Ordered frame receiver state");
                _verified = Attach(probe, client, id);
                _sender = Create<FrameProbeIdentity>("Ordered frame sender state");
                Attach(_sender, _server, id);
                _frames = Get<List<PlayerPacker>>(_server, "_clientFrames");
                _frames.Add(new PlayerPacker
                {
                    player = _player, packer = BitPackerPool.Get(), lastFullFrameSentTick = 8, lastSentFrameTick = 10
                });
                _input = new PredictionManager.InputQueue { ackedServerTick = 8 };
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(_server, "_clientTicks").Add(_player, _input);
            }

            public ulong ClientAck => Get<ulong>(client, "_ackedServerTick");
            public ulong ClientVerified => Get<ulong>(client, "_verifiedServerTick");
            public bool AwaitingFull => Get<bool>(client, "_awaitingFullFrame");
            public bool ResyncPending => Get<bool>(client, "_historyResyncPending");
            public ulong ServerAck => _input.ackedServerTick;
            public PlayerPacker ServerFrame => _frames[0];
            public void Request(ulong failed) => Invoke(_server, "HandleHistoryResyncRequest", _player, failed);
            public void ClearResyncCooldown() => Get<Dictionary<PlayerID, double>>(_server, "_historyResyncServedAt").Clear();
            public void Drain() => Invoke(client, "ProcessQueuedFrames", true);
            public FULL_STATE<FrameProbeState> Verified(ulong tick)
            {
                Assert.That(_verified.Read(tick, out var state), Is.True, $"No verified snapshot at {tick}");
                return state;
            }

            // Prepares the tick's frame and hands it to the stream exactly as SendPreparedServerFrame does.
            public Packet Prepare(ulong tick, int value, bool full = false, PlayerID? owner = null)
            {
                PrepareInterveningTicks(tick);
                Set(_server, "<localTick>k__BackingField", tick);
                Set(_server, "<localTickInContext>k__BackingField", tick);
                _sender.fullPredictedState = State(value, owner);
                CapturePreparedTick(tick);
                var frame = _frames[0];
                if (full) frame.requiresFullCheckpoint = true;
                _frames[0] = frame;
                Invoke(_server, "WriteInitialFrameToOthers");
                Assert.That(_frames[0].preparedFrameTick, Is.EqualTo(tick));
                Invoke(_server, "WriteEventHandles");
                frame = _frames[0];
                var packet = new Packet(tick, frame.preparedBaselineTick, frame.fullFrame, frame.packer);
                if (frame.fullFrame) frame.BeginFullFrame(tick);
                frame.lastSentFrameTick = tick;
                frame.fullFrame = false;
                frame.preparedFrameTick = 0;
                _frames[0] = frame;
                return packet;
            }

            public void Receive(Packet packet)
            {
                var received = Copy(packet.payload);
                try
                {
                    int bytes = received.ToByteData().length;
                    received.ResetPositionAndMode(true);
                    Invoke(client, "HandleFrameFromServer", packet.tick, packet.baseline, packet.tick, packet.full,
                        false, default(PackedInt), false, default(PackedInt), new BitPackerWithLength(bytes, received));
                }
                catch { received.Dispose(); throw; }
            }

            public void DeliverAck(ulong ack) => Invoke(_server, "ReceivedInput", 0UL, 0u, ack,
                BitPackerPool.Get(), new RPCInfo { sender = _player });

            private void PrepareInterveningTicks(ulong tick)
            {
                for (ulong prepared = _lastPreparedTick + 1; prepared < tick; prepared++)
                {
                    Set(_server, "<localTick>k__BackingField", prepared);
                    Set(_server, "<localTickInContext>k__BackingField", prepared);
                    CapturePreparedTick(prepared);
                }
            }

            private void CapturePreparedTick(ulong tick)
            {
                Invoke(_server, "CaptureInputHistory", tick);
                Invoke(_server, "CaptureLifecycleHistory", tick);
                _lastPreparedTick = tick;
            }

            private T Create<T>(string name) where T : Component
            {
                var go = new GameObject(name);
                _objects.Add(go);
                return go.AddComponent<T>();
            }

            private PredictionManager CreateManager(string name)
            {
                var manager = Create<PredictionManager>(name);
                Set(manager, "<tickRate>k__BackingField", 60);
                Set(manager, "<tickDelta>k__BackingField", 1f / 60);
                Set(manager, "<localTick>k__BackingField", 40UL);
                Set(manager, "<localTickInContext>k__BackingField", 40UL);
                Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(manager, "_updateViewMode", UpdateViewMode.None);
                return manager;
            }

            private static History<FULL_STATE<FrameProbeState>> Attach(
                FrameProbeIdentity identity, PredictionManager manager, PredictedComponentID id)
            {
                identity.Attach(manager, id);
                identity.fullPredictedState = State(900);
                var predicted = new History<FULL_STATE<FrameProbeState>>(200);
                predicted.Write(0, State(900));
                Set(typeof(PredictedIdentity<FrameProbeState>), identity, "_stateHistory", predicted);
                var verified = manager.GetVerifiedHistory<FULL_STATE<FrameProbeState>>(id, out _);
                verified.Write(8, State(80));
                verified.Write(10, State(100));
                Set(typeof(PredictedIdentity<FrameProbeState>), identity, "_verifiedHistory", verified);
                identity.lastVerifiedTick = 10;
                Get<List<PredictedIdentity>>(manager, "_systems").Add(identity);
                Set(manager, "_systemsCount", 1);
                Set(manager, "_inputHistorySystems", 1);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Add(id, identity);
                return verified;
            }

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                // Manager cleanup owns queued packet lifetimes. Detach the probes first so that
                // Editor-only lifecycle callbacks cannot hide missing state disposal.
                foreach (var manager in new[] { client, _server })
                {
                    Get<List<PredictedIdentity>>(manager, "_systems").Clear();
                    Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Clear();
                    Set(manager, "_systemsCount", 0);
                }
                probe.ReleasePredictionStateForPool();
                _sender.ReleasePredictionStateForPool();
                Invoke(client, "CleanupAllSystems");
                Invoke(_server, "CleanupAllSystems");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                for (int i = _objects.Count - 1; i >= 0; i--) Object.DestroyImmediate(_objects[i]);
            }
        }

        private static FULL_STATE<FrameProbeState> State(int value, PlayerID? owner = null)
        {
            var result = new FULL_STATE<FrameProbeState> { state = new FrameProbeState { value = value } };
            result.prediction.wasOnSimulationStartCalled = true;
            result.prediction.owner = owner;
            return result;
        }

        private static BitPacker Copy(BitPacker source)
        {
            int end = source.positionInBits;
            int bytes = source.ToByteData().length;
            var copy = BitPackerPool.Get();
            source.ResetPositionAndMode(true);
            copy.WriteBits(source, bytes * 8);
            source.SetBitPosition(end);
            return copy;
        }

        private static T Get<T>(PredictionManager manager, string name) => (T)Field(typeof(PredictionManager), name).GetValue(manager);
        private static void Set(PredictionManager manager, string name, object value) => Set(typeof(PredictionManager), manager, name, value);
        private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
        private static FieldInfo Field(Type type, string name)
        {
            var field = type.GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return field;
        }
        private static object Invoke(PredictionManager manager, string name, params object[] args)
        {
            var method = typeof(PredictionManager).GetMethod(name, Fields);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            return method.Invoke(manager, args);
        }
    }

    public struct FrameProbeState : IPredictedData<FrameProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class FrameProbeIdentity : PredictedIdentity<FrameProbeState>
    {
        public int deltaReads, inputReads, lastInput;
        public bool failFull;
        public ulong failSaveTick;
        public override bool hasInput => true;
        internal override bool HasInputAt(ulong tick) => true;
        public override void WriteFirstInput(ulong tick, BitPacker packer) => Packer<int>.Write(packer, (int)tick * 10);
        public override void ReadFirstInput(ulong tick, BitPacker packer)
        {
            inputReads++;
            lastInput = Packer<int>.Read(packer);
        }
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager; id = componentId; myType = GetType();
        }
        protected override void Simulate(ref FrameProbeState state, float delta) => state.value++;
        protected override void WriteDeltaState(BitPacker packer, in FrameProbeState baseline, in FrameProbeState current)
            => Packer<int>.Write(packer, current.value - baseline.value);
        protected override void ReadDeltaState(BitPacker packer, in FrameProbeState baseline, ref FrameProbeState state)
        {
            deltaReads++;
            state.value = baseline.value + Packer<int>.Read(packer);
        }
        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            if (failFull) throw new InvalidOperationException("checkpoint test full decode failure");
            base.ReadFirstState(tick, packer, serverTick);
        }
        internal override void SaveStateInHistory(ulong tick)
        {
            if (tick == failSaveTick) throw new InvalidOperationException("checkpoint test entering state failure");
            base.SaveStateInHistory(tick);
        }
    }
}
