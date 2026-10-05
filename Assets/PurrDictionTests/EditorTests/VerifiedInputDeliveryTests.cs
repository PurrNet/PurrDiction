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
    // Uses the real input identity, server frame writer, receive queue and verified replay.
    // Only delivery is simulated; corruptions remove records from actual emitted packets.
    public sealed class VerifiedInputDeliveryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(VerifiedInputProbeState));
            Hasher.PrepareType(typeof(VerifiedInputProbeInput));
            Packer<VerifiedInputProbeState>.RegisterWriter((p, value) => Packer<int>.Write(p, value.value));
            Packer<VerifiedInputProbeState>.RegisterReader((BitPacker p, ref VerifiedInputProbeState value) =>
                value.value = Packer<int>.Read(p));
            Packer<VerifiedInputProbeInput>.RegisterWriter((p, value) => Packer<int>.Write(p, value.value));
            Packer<VerifiedInputProbeInput>.RegisterReader((BitPacker p, ref VerifiedInputProbeInput value) =>
                value.value = Packer<int>.Read(p));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingAuthoritativeInputCannotVerifySpeculativeInputAlreadyInHistory(bool full)
        {
            using var f = new Fixture();
            f.clientInputs.Write(11, new VerifiedInputProbeInput { value = 999 });
            Assert.That(f.probe.HasInputAt(11), Is.True,
                "the real input history already contains a speculative value for this tick");
            using var complete = f.Prepare(11, full);
            using var incomplete = f.WithoutInput(complete);
            LogAssert.Expect(LogType.Error, new Regex("Cannot apply (full )?prediction frame 11:"));
            f.Receive(incomplete);
            f.Drain();
            Assert.That(f.VerifiedTick, Is.EqualTo(10));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(10));
            Assert.That(f.probe.verifiedInputs, Is.Empty,
                "no simulation may label the speculative value as authoritative");
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
        }

        [Test]
        public void FrameCoveringSeveralTicksReplaysEachWithItsAuthoritativeInput()
        {
            using var f = new Fixture();
            for (ulong tick = 11; tick <= 14; tick++)
                f.clientInputs.Write(tick, new VerifiedInputProbeInput { value = 999 });
            // A server update rate below the tick rate sends one frame for ticks 11-14.
            using var coalesced = f.Prepare(14);
            f.Receive(coalesced);
            f.Drain();
            Assert.That(f.VerifiedTick, Is.EqualTo(14));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(14));
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(new[] { "11:110", "12:120", "13:130", "14:140" }),
                "ordinary predicted identities must consume every covered authoritative input on its original tick");
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PartialReplayFailureWaitsForFullWithoutRepeatingEarlierVerifiedCallbacks(bool sameBatch)
        {
            using var f = new Fixture();
            using var complete = f.Prepare(14);
            using var incomplete = f.WithoutInput(complete, 12);
            using var following = f.Prepare(15);
            LogAssert.Expect(LogType.Error, new Regex("Cannot apply prediction frame 14:.*tick 12"));
            f.Receive(incomplete);
            if (sameBatch)
                f.Receive(following);
            f.Drain();
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(new[] { "11:110" }),
                "tick 11 completed before the missing input at tick 12 was discovered");
            Assert.That(f.VerifiedTick, Is.EqualTo(10));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(10));
            if (!sameBatch)
            {
                f.Receive(following);
                f.Drain();
            }
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(new[] { "11:110" }),
                "a later delta must not dispatch tick 11 again after partial replay failed");
            Assert.That(f.VerifiedTick, Is.EqualTo(10));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(10));

            using var checkpoint = f.Prepare(16, full: true);
            f.Receive(checkpoint);
            f.Drain();
            Assert.That(f.VerifiedTick, Is.EqualTo(16));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(16));
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.False);
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(new[] { "11:110", "16:160" }),
                "the checkpoint replaces the incomplete interval instead of redispatching it");

            using var resumed = f.Prepare(17);
            f.Receive(resumed);
            f.Drain();
            Assert.That(f.AcknowledgedTick, Is.EqualTo(17));
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(new[] { "11:110", "16:160", "17:170" }),
                "ordinary verified input delivery resumes after the checkpoint succeeds");
        }

        [TestCase(14UL, false)]
        [TestCase(14UL, true)]
        [TestCase(15UL, false)]
        [TestCase(15UL, true)]
        public void EnteringStateFailureWaitsForFullWithoutRepeatingVerifiedCallbacks(ulong failSaveTick, bool sameBatch)
        {
            using var f = new Fixture();
            using var interrupted = f.Prepare(14);
            using var following = f.Prepare(15);
            f.probe.failSaveTick = failSaveTick;
            LogAssert.Expect(LogType.Error, new Regex("Cannot apply prediction frame 14:.*verified input test entering state failure"));
            f.Receive(interrupted);
            if (sameBatch)
                f.Receive(following);
            Assert.DoesNotThrow(() => f.Drain(), "an escaping state-save failure must enter checkpoint recovery");

            var expected = new List<string> { "11:110", "12:120", "13:130" };
            if (failSaveTick == 15)
                expected.Add("14:140");
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(expected),
                "the failure follows historical simulation, and may follow the newest verified simulation too");
            Assert.That(f.VerifiedTick, Is.EqualTo(10));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(10));
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
            Assert.That(Get<bool>(f.client, "_awaitingFullFrame"), Is.True);
            if (!sameBatch)
            {
                f.Receive(following);
                f.Drain();
            }
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(expected),
                "the one-shot fault has cleared, but ordinary frames cannot redispatch completed verified ticks");
            Assert.That(f.VerifiedTick, Is.EqualTo(10));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(10));

            using var checkpoint = f.Prepare(16, full: true);
            f.Receive(checkpoint);
            f.Drain();
            expected.Add("16:160");
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(expected));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(16));
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.False);
            Assert.That(Get<bool>(f.client, "_awaitingFullFrame"), Is.False);

            using var resumed = f.Prepare(17);
            f.Receive(resumed);
            f.Drain();
            expected.Add("17:170");
            Assert.That(f.probe.verifiedInputs, Is.EqualTo(expected));
            Assert.That(f.AcknowledgedTick, Is.EqualTo(17));
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
            private readonly VerifiedInputProbeIdentity _sender;
            private readonly History<VerifiedInputProbeInput> _serverInputs;
            private readonly List<PlayerPacker> _frames;
            private readonly double _previousCadence;
            private ulong _lastPreparedTick = 8;
            public readonly PredictionManager client;
            public readonly VerifiedInputProbeIdentity probe;
            public readonly History<VerifiedInputProbeInput> clientInputs;

            public Fixture()
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = Create<NetworkManager>("Verified input network");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                client = CreateManager("Verified input receiver");
                _server = CreateManager("Verified input sender");
                Set(typeof(NetworkIdentity), client, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), client, "_isSpawnedClient", false);
                Set(client, "_verifiedServerTick", 10UL);
                Set(client, "_latestFrameServerTick", 10UL);
                Set(client, "_ackedServerTick", 10UL);
                Set(client, "_awaitingFullFrame", false);
                Set(_server, "<cachedIsServer>k__BackingField", true);
                var id = new PredictedComponentID(new PredictedObjectID(2933), 0);
                probe = Create<VerifiedInputProbeIdentity>("Verified input receiver identity");
                clientInputs = Attach(probe, client, id);
                _sender = Create<VerifiedInputProbeIdentity>("Verified input sender identity");
                _serverInputs = Attach(_sender, _server, id);
                _frames = Get<List<PlayerPacker>>(_server, "_clientFrames");
                _frames.Add(new PlayerPacker { player = default, packer = BitPackerPool.Get(), lastFullFrameSentTick = 8, lastSentFrameTick = 8 });
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(_server, "_clientTicks")
                    .Add(default, new PredictionManager.InputQueue { ackedServerTick = 8 });
            }

            public ulong VerifiedTick => Get<ulong>(client, "_verifiedServerTick");
            public ulong AcknowledgedTick => Get<ulong>(client, "_ackedServerTick");
            public void Drain() => Invoke(client, "ProcessQueuedFrames", true);

            public Packet Prepare(ulong tick, bool full = false)
            {
                // Advance all skipped server ticks before constructing the requested packet.
                // Their authoritative inputs are captured before any later tick is prepared.
                for (ulong prepared = _lastPreparedTick + 1; prepared <= tick; prepared++)
                {
                    Set(_server, "<localTick>k__BackingField", prepared);
                    Set(_server, "<localTickInContext>k__BackingField", prepared);
                    _sender.fullPredictedState = State((int)prepared * 10);
                    _serverInputs.Write(prepared, new VerifiedInputProbeInput { value = (int)prepared * 10 });
                    Invoke(_server, "CaptureInputHistory", prepared);
                    Invoke(_server, "CaptureLifecycleHistory", prepared);
                    _lastPreparedTick = prepared;
                }
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

            public Packet WithoutInput(Packet original, ulong? omittedTick = null)
            {
                ulong targetTick = omittedTick ?? original.tick;
                Assert.That(targetTick, Is.InRange(original.baseline + 1, original.tick));
                if (original.full)
                    Assert.That(targetTick, Is.EqualTo(original.tick));
                var source = original.payload;
                int end = source.positionInBits;
                source.ResetPositionAndMode(true);
                if (original.full)
                {
                    Packer<PackedInt>.Read(source);
                    Packer<float>.Read(source);
                    Packer<uint>.Read(source);
                }
                Assert.That(Packer<PackedUInt>.Read(source).value, Is.Zero);
                Assert.That(Packer<bool>.Read(source), Is.False);
                if (original.full)
                    AddressedPredictionRecords.SkipSection(source, end);
                int inputStart = source.positionInBits;
                using var altered = BitPackerPool.Get();
                altered.WriteBitDataWithoutConsumingIt(new BitData(source, 0, inputStart));
                if (original.full)
                {
                    AddressedPredictionRecords.SkipSection(source, end);
                    AddressedPredictionRecords.WriteSectionCount(0, altered);
                }
                else
                {
                    uint tickCount = Packer<PackedUInt>.Read(source).value;
                    Assert.That(tickCount, Is.EqualTo(original.tick - original.baseline));
                    Packer<PackedUInt>.Write(altered, tickCount);
                    // Decode every tick (repeats resolve against the previous decoded tick) and
                    // re-emit the canonical no-repeat layout, since byte padding depends on position.
                    var previous = new List<(PredictedComponentID id, BitPacker payload)>();
                    var current = new List<(PredictedComponentID id, BitPacker payload)>();
                    var previousOffsets = new List<(PlayerID player, uint value)>();
                    var offsets = new List<(PlayerID player, uint value)>();
                    bool previousSourceHadEntries = false;
                    bool previousEmittedHadEntries = false;
                    for (uint tick = 0; tick < tickCount; tick++)
                    {
                        uint entryCount = Packer<PackedUInt>.Read(source).value;
                        var repeats = new bool[entryCount];
                        var deltas = new bool[entryCount];
                        bool sameRoster = false;
                        current.Clear();
                        offsets.Clear();
                        if (entryCount > 0)
                        {
                            sameRoster = tick > 0 && Packer<bool>.Read(source);
                            uint offsetCount = Packer<PackedUInt>.Read(source).value;
                            if (offsetCount > 0)
                            {
                                bool sameOffsetRoster = previousSourceHadEntries && Packer<bool>.Read(source);
                                for (uint i = 0; i < offsetCount; i++)
                                    offsets.Add((sameOffsetRoster ? previousOffsets[(int)i].player : Packer<PlayerID>.Read(source), 0u));
                                for (int i = 0; i < offsets.Count; i++)
                                    offsets[i] = (offsets[i].player, (uint)source.ReadBits((byte)PredictionManager.ViewOffsetBits));
                            }
                            if (sameRoster)
                                for (uint i = 0; i < entryCount; i++)
                                {
                                    repeats[i] = Packer<bool>.Read(source);
                                    deltas[i] = !repeats[i] && Packer<bool>.Read(source);
                                    if (!repeats[i] && !deltas[i])
                                        Assert.That(Packer<bool>.Read(source), Is.False, "no receiver-owned input to restore");
                                }
                            source.SkipBits((8 - source.positionInBits % 8) % 8);
                        }
                        for (uint i = 0; i < entryCount; i++)
                        {
                            if (repeats[i])
                            {
                                current.Add(previous[(int)i]);
                                continue;
                            }
                            var id = sameRoster ? previous[(int)i].id : Packer<PredictedComponentID>.Read(source);
                            if (!sameRoster)
                                Assert.That(Packer<bool>.Read(source), Is.False, "no receiver-owned input to restore");
                            var payload = BitPackerPool.Get();
                            if (deltas[i])
                                InputHistoryDelta.Read(source, end, new BitData(previous[(int)i].payload), payload);
                            else
                            {
                                int bits = checked((int)Packer<PackedUInt>.Read(source).value);
                                payload.WriteBitDataWithoutConsumingIt(new BitData(source, source.positionInBits, bits));
                                source.SkipBits(bits);
                            }
                            current.Add((id, payload));
                        }

                        bool omitted = original.baseline + tick + 1 == targetTick;
                        if (omitted)
                        {
                            Assert.That(entryCount, Is.EqualTo(1));
                            Packer<PackedUInt>.Write(altered, 0u);
                        }
                        else
                        {
                            Packer<PackedUInt>.Write(altered, entryCount);
                            if (entryCount > 0)
                            {
                                if (tick > 0)
                                    Packer<bool>.Write(altered, false);
                                Packer<PackedUInt>.Write(altered, (uint)offsets.Count);
                                if (offsets.Count > 0)
                                {
                                    if (previousEmittedHadEntries)
                                        Packer<bool>.Write(altered, false);
                                    foreach (var (player, _) in offsets)
                                        Packer<PlayerID>.Write(altered, player);
                                    foreach (var (_, value) in offsets)
                                        altered.WriteBits(value, (byte)PredictionManager.ViewOffsetBits);
                                }
                                altered.WriteBits(0UL, (byte)((8 - altered.positionInBits % 8) % 8));
                                foreach (var (id, payload) in current)
                                {
                                    Packer<PredictedComponentID>.Write(altered, id);
                                    Packer<bool>.Write(altered, false);
                                    Packer<PackedUInt>.Write(altered, (uint)payload.positionInBits);
                                    altered.WriteBitDataWithoutConsumingIt(new BitData(payload, 0, payload.positionInBits));
                                }
                            }
                        }
                        previous.Clear();
                        previous.AddRange(current);
                        previousOffsets.Clear();
                        previousOffsets.AddRange(offsets);
                        previousSourceHadEntries = entryCount > 0;
                        previousEmittedHadEntries = !omitted && entryCount > 0;
                    }
                }
                altered.WriteBitDataWithoutConsumingIt(
                    new BitData(source, source.positionInBits, end - source.positionInBits));
                source.SetBitPosition(end);
                return new Packet(original.tick, original.baseline, original.full, altered);
            }

            public void Receive(Packet packet)
            {
                var received = Copy(packet.payload);
                try
                {
                    int bytes = received.ToByteData().length;
                    received.ResetPositionAndMode(true);
                    Invoke(client, "HandleFrameFromServer", packet.tick, packet.baseline,
                        packet.tick, packet.full, false, default(PackedInt), false, default(PackedInt),
                        new BitPackerWithLength(bytes, received));
                }
                catch { received.Dispose(); throw; }
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
                Set(manager, "<localTick>k__BackingField", 20UL);
                Set(manager, "<localTickInContext>k__BackingField", 20UL);
                Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(manager, "_updateViewMode", UpdateViewMode.None);
                return manager;
            }

            private static History<VerifiedInputProbeInput> Attach(
                VerifiedInputProbeIdentity identity, PredictionManager manager, PredictedComponentID id)
            {
                identity.Attach(manager, id);
                identity.fullPredictedState = State(900);
                var predicted = new History<FULL_STATE<VerifiedInputProbeState>>(200);
                predicted.Write(0, State(900));
                predicted.Write(11, State(100));
                Set(typeof(PredictedIdentity<VerifiedInputProbeState>), identity, "_stateHistory", predicted);
                var verified = manager.GetVerifiedHistory<FULL_STATE<VerifiedInputProbeState>>(id, out _);
                verified.Write(8, State(80));
                verified.Write(10, State(100));
                Set(typeof(PredictedIdentity<VerifiedInputProbeState>), identity, "_verifiedHistory", verified);
                var inputs = new History<VerifiedInputProbeInput>(200);
                Set(typeof(PredictedIdentity<VerifiedInputProbeInput, VerifiedInputProbeState>), identity, "_inputHistory", inputs);
                identity.lastVerifiedTick = 10;
                Get<List<PredictedIdentity>>(manager, "_systems").Add(identity);
                Set(manager, "_systemsCount", 1);
                Set(manager, "_inputHistorySystems", 1);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Add(id, identity);
                return inputs;
            }

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
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

        private static FULL_STATE<VerifiedInputProbeState> State(int value)
        {
            var result = new FULL_STATE<VerifiedInputProbeState> { state = new VerifiedInputProbeState { value = value } };
            result.prediction.wasOnSimulationStartCalled = true;
            return result;
        }

        private static BitPacker Copy(BitPacker source)
        {
            int end = source.positionInBits;
            var copy = BitPackerPool.Get();
            copy.WriteBitDataWithoutConsumingIt(new BitData(source, 0, end));
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

    public struct VerifiedInputProbeInput : IPredictedData
    {
        public int value;
        public void Dispose() { }
    }

    public struct VerifiedInputProbeState : IPredictedData<VerifiedInputProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class VerifiedInputProbeIdentity : PredictedIdentity<VerifiedInputProbeInput, VerifiedInputProbeState>
    {
        public readonly List<string> verifiedInputs = new();
        public ulong failSaveTick;
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager; id = componentId; myType = GetType();
            extrapolateInput = false;
        }
        protected override void Simulate(VerifiedInputProbeInput input, ref VerifiedInputProbeState state, float delta)
        {
            state.value += input.value;
            if (predictionManager.isVerified && predictionManager.isReplaying)
                verifiedInputs.Add($"{predictionManager.localTickInContext}:{input.value}");
        }
        protected override void WriteDeltaState(BitPacker packer, in VerifiedInputProbeState baseline, in VerifiedInputProbeState current)
            => Packer<int>.Write(packer, current.value - baseline.value);
        protected override void ReadDeltaState(BitPacker packer, in VerifiedInputProbeState baseline, ref VerifiedInputProbeState state)
            => state.value = baseline.value + Packer<int>.Read(packer);
        internal override void SaveStateInHistory(ulong tick)
        {
            if (tick == failSaveTick && predictionManager.isVerifiedAndReplaying)
            {
                failSaveTick = 0;
                throw new InvalidOperationException("verified input test entering state failure");
            }
            base.SaveStateInHistory(tick);
        }
    }
}
