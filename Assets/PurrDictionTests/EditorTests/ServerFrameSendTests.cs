using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Exercise production dispatch/flush and generated RPC serialization. The existing RPC
    // preprocessor observes a copy after compression, before the deliberately unspawned
    // fixture is rejected by RPC send validation. These are submission tests, not sockets
    // or replay tests; frame bodies are markers and historical replay has separate coverage.
    public sealed class ServerFrameSendTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [Test]
        public void CatchupSubmitsOnlyNewestPreparedTickAndDoesNotRepeatOnAnotherFlush()
        {
            using var f = new Fixture();
            for (ulong tick = 10; tick <= 14; tick++)
            {
                f.Prepare(tick, 8, (uint)(tick * 100));
                f.Dispatch();
                Assert.That(f.sent, Is.Empty, "ordinary ticks must wait for the render-phase flush");
            }

            f.AdvancePast(14);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(1));
            Assert.That(f.sent[0].tick, Is.EqualTo(14));
            Assert.That(f.sent[0].baseline, Is.EqualTo(8));
            Assert.That(f.sent[0].marker, Is.EqualTo(1400));
            Assert.That(f.sent[0].channel, Is.EqualTo(Channel.ReliableOrdered));
            Assert.That(f.sent[0].immediate, Is.False);
            Assert.That(f.Frame.sentVisibilityTick, Is.EqualTo(14));
            Assert.That(f.Frame.frameSendSchedule.lastSentTick, Is.EqualTo(14));
            Assert.That(f.Frame.preparedFrameTick, Is.Zero);

            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(1));
        }

        [Test]
        public void RateLimitedFrameKeepsFeedbackPendingAndLaterFrameUsesFreshFeedbackWithItsCapturedBaseline()
        {
            using var f = new Fixture();
            f.manager.serverUpdateRate = 30;
            f.Prepare(10, 8, 1000);
            f.Dispatch();
            f.AdvancePast(10);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(1));

            f.input.hasPendingInputSlack = true;
            f.input.pendingInputSlackMs = 17;
            f.Prepare(11, 8, 1100);
            f.Dispatch();
            f.AdvancePast(11);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(1));
            Assert.That(f.input.hasPendingInputSlack, Is.True,
                "a cadence skip must not consume the next pacing sample");
            Assert.That(f.Frame.preparedFrameTick, Is.EqualTo(11));

            f.Prepare(12, 8, 1200);
            f.Dispatch();
            f.AdvancePast(12);
            // Immediate input receive runs after tick preparation in NetworkManager.Update.
            f.input.ackedServerTick = 10;
            f.input.lastConsumedTick = 12;
            f.QueueInput(13);
            f.QueueInput(14);
            f.input.rawHighestReceivedTick = 15;
            f.input.pendingInputSlackMs = 23;
            f.Flush();

            Assert.That(f.sent, Has.Count.EqualTo(2));
            var sent = f.sent[1];
            Assert.That(sent.tick, Is.EqualTo(12));
            Assert.That(sent.baseline, Is.EqualTo(8), "the serialized body still uses its captured baseline");
            Assert.That(sent.inputAck, Is.EqualTo(14), "include contiguous input received after preparation");
            Assert.That(sent.hasInputMargin, Is.True);
            Assert.That(sent.inputMargin, Is.EqualTo(3), "margin is relative to tick 12, not the advanced local tick 13");
            Assert.That(sent.hasInputSlack, Is.True);
            Assert.That(sent.inputSlackMs, Is.EqualTo(23));
            Assert.That(f.input.hasPendingInputSlack, Is.False);
        }

        [Test]
        public void FullFrameBypassesCadenceAndTheNextDeltaContinuesIt()
        {
            using var f = new Fixture();
            f.manager.serverUpdateRate = 30;
            f.Prepare(10, 8, 1000);
            f.Dispatch();
            f.AdvancePast(10);
            f.Flush();

            f.Prepare(11, 8, 1100, full: true);
            f.Dispatch();
            Assert.That(f.sent, Has.Count.EqualTo(2), "full submission happens in the tick itself");
            Assert.That(f.sent[1].full, Is.True);
            Assert.That(f.sent[1].channel, Is.EqualTo(Channel.ReliableOrdered));
            Assert.That(f.Frame.lastFullFrameSentTick, Is.EqualTo(11));
            Assert.That(f.Frame.lastSentFrameTick, Is.EqualTo(11));
            Assert.That(f.Frame.requiresFullCheckpoint, Is.False);

            f.Prepare(12, 11, 1200);
            f.Dispatch();
            f.AdvancePast(12);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(2), "the full rebases the ordinary cadence");
            f.Prepare(13, 11, 1300);
            f.Dispatch();
            f.AdvancePast(13);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(3));
            Assert.That(f.sent[2].full, Is.False);
            Assert.That(f.sent[2].baseline, Is.EqualTo(11));
            Assert.That(f.Frame.lastSentFrameTick, Is.EqualTo(13));
            Assert.That(f.manager.fullFramesSentTotal, Is.EqualTo(1));
        }

        [Test]
        public void DisabledManagerSubmitsDueFramesFromTicksAndStillHonorsRate()
        {
            using var f = new Fixture();
            f.manager.serverUpdateRate = 30;
            f.manager.enabled = false;
            f.Prepare(10, 8, 1000);
            f.Dispatch();
            Assert.That(f.sent, Has.Count.EqualTo(1));
            f.Prepare(11, 8, 1100);
            f.Dispatch();
            Assert.That(f.sent, Has.Count.EqualTo(1));
            f.Prepare(12, 8, 1200);
            f.Dispatch();
            Assert.That(f.sent, Has.Count.EqualTo(2));
            Assert.That(f.sent[1].tick, Is.EqualTo(12));
        }

        [Test]
        public void FlushWaitsUntilSimulationAndReplayHaveFinished()
        {
            using var f = new Fixture();
            f.Prepare(10, 8, 1000);
            f.Dispatch();
            f.AdvancePast(10);
            Set(f.manager, "<isSimulating>k__BackingField", true);
            f.Flush();
            Assert.That(f.sent, Is.Empty);
            Set(f.manager, "<isSimulating>k__BackingField", false);
            Set(f.manager, "<isReplaying>k__BackingField", true);
            f.Flush();
            Assert.That(f.sent, Is.Empty);
            Set(f.manager, "<isReplaying>k__BackingField", false);
            f.Flush();
            Assert.That(f.sent, Has.Count.EqualTo(1));
        }

        [Test]
        public void RecoveryRequestInvalidatesPreparedOrdinaryFrameBeforeFlush()
        {
            using var f = new Fixture();
            f.Prepare(10, 8, 1000);
            f.Dispatch();
            f.AdvancePast(10);
            Assert.That(Invoke(f.manager, "QueueFullResync", f.player), Is.True);
            f.Flush();
            Assert.That(f.sent, Is.Empty);
            Assert.That(f.Frame.requiresFullCheckpoint, Is.True);
            Assert.That(f.Frame.preparedFrameTick, Is.Zero);
        }

        private sealed class SubmittedFrame
        {
            public ulong tick, baseline, inputAck;
            public bool full, hasInputMargin, hasInputSlack, immediate;
            public int inputMargin, inputSlackMs;
            public uint marker;
            public Channel channel;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _object;
            private readonly List<PlayerPacker> _frames;
            public readonly PredictionManager manager;
            public readonly PlayerID player = new(new PackedULong(7), false);
            public readonly PredictionManager.InputQueue input = new() { ackedServerTick = 8 };
            public readonly List<SubmittedFrame> sent = new();

            public Fixture()
            {
                _object = new GameObject("Server frame submission test");
                manager = _object.AddComponent<PredictionManager>();
                Set(manager, "<tickRate>k__BackingField", 60);
                Set(manager, "<tickDelta>k__BackingField", 1f / 60f);
                Set(manager, "<cachedIsServer>k__BackingField", true);
                _frames = Get<List<PlayerPacker>>(manager, "_clientFrames");
                _frames.Add(new PlayerPacker
                {
                    player = player,
                    packer = BitPackerPool.Get(),
                    lastFullFrameSentTick = 8
                });
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(manager, "_clientTicks").Add(player, input);
                RPCModule.onPreProcessRpc += Capture;
                // Frames ride the batched ordered lane, which reports this unspawned manager on every send.
                LogAssert.ignoreFailingMessages = true;
            }

            public PlayerPacker Frame => _frames[0];
            public void SetFrame(PlayerPacker frame) => _frames[0] = frame;

            public void Prepare(ulong tick, ulong baseline, uint marker, bool full = false)
            {
                Set(manager, "<localTick>k__BackingField", tick);
                Set(manager, "<localTickInContext>k__BackingField", tick);
                var frame = Frame;
                frame.packer.ResetPositionAndMode(false);
                Packer<uint>.Write(frame.packer, marker);
                frame.preparedFrameTick = tick;
                frame.preparedBaselineTick = baseline;
                frame.preparedVisibilityTick = tick;
                frame.fullFrame = full;
                frame.requiresFullCheckpoint = full;
                SetFrame(frame);
            }

            public void AdvancePast(ulong tick)
            {
                Set(manager, "<localTick>k__BackingField", tick + 1);
                Set(manager, "<localTickInContext>k__BackingField", tick + 1);
            }

            public void QueueInput(ulong tick) => input.byTick.Add(tick,
                new PredictionManager.InputQueueValue { clientTick = tick, inputPacket = BitPackerPool.Get() });

            public void Dispatch() => Invoke(manager, "DispatchPreparedServerFrames");
            public void Flush() => Invoke(manager, "FlushPendingServerFrames");

            private void Capture(RPCSignature signature, ref BitPacker compressed)
            {
                if (signature.rpcName != "SendFrameToRemote")
                    return;
                Assert.That(signature.targetPlayer, Is.EqualTo(player));
                using var decoded = BitPackerPool.Get();
                decoded.UnpickleFrom(compressed);
                decoded.ResetPositionAndMode(true);
                var frame = new SubmittedFrame
                {
                    channel = signature.channel,
                    immediate = signature.immediate,
                    tick = Packer<ulong>.Read(decoded),
                    baseline = Packer<ulong>.Read(decoded),
                    inputAck = Packer<ulong>.Read(decoded),
                    full = Packer<bool>.Read(decoded),
                    hasInputMargin = Packer<bool>.Read(decoded),
                    inputMargin = Packer<PackedInt>.Read(decoded),
                    hasInputSlack = Packer<bool>.Read(decoded),
                    inputSlackMs = Packer<PackedInt>.Read(decoded)
                };
                using var payload = Packer<BitPackerWithLength>.Read(decoded);
                Assert.That(payload.originalLength, Is.EqualTo(sizeof(uint)));
                payload.packer.ResetPositionAndMode(true);
                frame.marker = Packer<uint>.Read(payload.packer);
                sent.Add(frame);
            }

            public void Dispose()
            {
                RPCModule.onPreProcessRpc -= Capture;
                LogAssert.ignoreFailingMessages = false;
                Set(manager, "<cachedIsServer>k__BackingField", false);
                Invoke(manager, "CleanupAllSystems");
                Object.DestroyImmediate(_object);
            }
        }

        private static T Get<T>(PredictionManager manager, string name)
            => (T)Field(name).GetValue(manager);

        private static void Set(PredictionManager manager, string name, object value)
            => Field(name).SetValue(manager, value);

        private static FieldInfo Field(string name)
        {
            var field = typeof(PredictionManager).GetField(name, Members);
            Assert.That(field, Is.Not.Null, $"Missing PredictionManager.{name}");
            return field;
        }

        private static object Invoke(PredictionManager manager, string name, params object[] arguments)
        {
            var method = typeof(PredictionManager).GetMethod(name, Members);
            Assert.That(method, Is.Not.Null, $"Missing PredictionManager.{name}");
            return method.Invoke(manager, arguments);
        }
    }
}
