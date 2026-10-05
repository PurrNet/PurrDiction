using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class FullCheckpointDeliveryTests
    {
        [Test]
        public void DuplicateObserverSyncPreservesTheAckQueueAndSentVisibility()
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var networkObject = new GameObject("Duplicate observer network");
            var predictionObject = new GameObject("Duplicate observer prediction");
            networkObject.SetActive(false);
            predictionObject.SetActive(false);
            PredictionManager manager = null;
            try
            {
                var transport = networkObject.AddComponent<LocalTransport>();
                var network = networkObject.AddComponent<NetworkManager>();
                network.transport = transport;
                typeof(NetworkManager).GetField("_serverPlayersManager", fields).SetValue(
                    network, new PlayersManager(network, null, null));
                manager = predictionObject.AddComponent<PredictionManager>();
                typeof(NetworkIdentity).GetField("<networkManager>k__BackingField", fields).SetValue(manager, network);

                var player = default(PlayerID);
                var frame = new PlayerPacker
                {
                    player = player, packer = BitPackerPool.Get(),
                    preparedFrameTick = 111, preparedBaselineTick = 100,
                    preparedVisibilityTick = 111, sentVisibilityTick = 110
                };
                frame.BeginFullFrame(100);
                var frames = (List<PlayerPacker>)typeof(PredictionManager).GetField("_clientFrames", fields).GetValue(manager);
                frames.Add(frame);
                var input = new PredictionManager.InputQueue { ackedServerTick = 90, lastConsumedTick = 85 };
                var inputs = (Dictionary<PlayerID, PredictionManager.InputQueue>)typeof(PredictionManager)
                    .GetField("_clientTicks", fields).GetValue(manager);
                inputs.Add(player, input);
                var cooldowns = (Dictionary<PlayerID, double>)typeof(PredictionManager)
                    .GetField("_historyResyncServedAt", fields).GetValue(manager);
                cooldowns.Add(player, 7d);
                var pending = (List<PlayerID>)typeof(PredictionManager).GetField("_pendingFullSync", fields).GetValue(manager);
                pending.Add(player);
                pending.Add(player);

                typeof(PredictionManager).GetMethod("FlushPendingFullSyncs", fields).Invoke(manager, null);

                Assert.That(frames.Count, Is.EqualTo(1));
                Assert.That(pending, Is.Empty);
                Assert.That(inputs[player], Is.SameAs(input));
                Assert.That(input.ackedServerTick, Is.EqualTo(90));
                Assert.That(input.lastConsumedTick, Is.EqualTo(85));
                Assert.That(cooldowns[player], Is.EqualTo(7d));
                var retained = frames[0];
                Assert.That(retained.lastFullFrameSentTick, Is.EqualTo(100));
                Assert.That(retained.sentVisibilityTick, Is.EqualTo(110));
                Assert.That(retained.requiresFullCheckpoint, Is.True);
                Assert.That(retained.preparedFrameTick, Is.Zero);
                Assert.That(retained.preparedBaselineTick, Is.Zero);
                Assert.That(retained.preparedVisibilityTick, Is.Zero);
                Assert.That(retained.packer, Is.SameAs(frame.packer));

            }
            finally
            {
                // Inactive components may never run OnDestroy; release their owned state explicitly.
                if (manager)
                {
                    var frames = (List<PlayerPacker>)typeof(PredictionManager)
                        .GetField("_clientFrames", fields).GetValue(manager);
                    foreach (var existing in frames)
                        existing.Dispose();
                    frames.Clear();
                }
                UnityEngine.Object.DestroyImmediate(predictionObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void BeginningAFullRecordsItAndSatisfiesTheRequest()
        {
            var frame = new PlayerPacker { requiresFullCheckpoint = true };
            frame.BeginFullFrame(100);

            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100));
            Assert.That(frame.requiresFullCheckpoint, Is.False);
            frame.BeginFullFrame(120);
            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(120),
                "an ordered stream can carry any number of fulls; none waits for another's delivery");
        }

        [Test]
        public void PreparingTheNextFrameReusesTheWorkingPacker()
        {
            var frame = new PlayerPacker { packer = BitPackerPool.Get() };
            try
            {
                frame.packer.WriteBytes(new byte[] { 0x7A, 0x00, 0xFF, 0xB3 });
                frame.BeginFullFrame(100);
                var originalPacker = frame.packer;

                // The RPC has serialized the full; its working buffer is immediately free for the next frame.
                frame.packer.ResetPositionAndMode(false);
                var continuation = new byte[] { 0xC1, 0x02 };
                frame.packer.WriteBytes(continuation);

                Assert.That(frame.packer, Is.SameAs(originalPacker));
                Assert.That(frame.packer.ToByteData().span.ToArray(), Is.EqualTo(continuation));
                Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100));
            }
            finally
            {
                frame.Dispose();
            }
        }

        [Test]
        public void DisposeReleasesPackerAndStreamPosition()
        {
            var frame = new PlayerPacker { packer = BitPackerPool.Get() };
            frame.BeginFullFrame(100);
            frame.lastSentFrameTick = 104;
            frame.requiresFullCheckpoint = true;
            frame.preparedBaselineTick = 100;

            frame.Dispose();

            Assert.That(frame.packer, Is.Null);
            Assert.That(frame.lastFullFrameSentTick, Is.Zero);
            Assert.That(frame.lastSentFrameTick, Is.Zero);
            Assert.That(frame.requiresFullCheckpoint, Is.False);
            Assert.That(frame.preparedBaselineTick, Is.Zero);
            Assert.DoesNotThrow(() => frame.Dispose());
        }
    }
}
