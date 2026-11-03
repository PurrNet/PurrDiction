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
    public sealed class UploadRoundTripTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) =>
                    value.id = Packer<int>.Read(packer));
        }

        [TestCase(1000UL, 33U)]
        [TestCase(1000UL, uint.MaxValue)]
        [TestCase(100UL, 33U)]
        [TestCase(100UL, uint.MaxValue)]
        [TestCase(ulong.MaxValue, 2U)]
        [TestCase(ulong.MaxValue - 30, 32U)]
        public void InvalidUploadRangeIsRejectedBeforeQueueMutation(ulong firstTick, uint tickCount)
        {
            var serverObject = new GameObject("Invalid upload range server");
            try
            {
                var server = CreateManager(serverObject);
                SetLocalTick(server, 100);
                var sender = new PlayerID(9, false);
                var queue = new PredictionManager.InputQueue
                {
                    ackedServerTick = 10,
                    rawHighestReceivedTick = 50,
                    highestReceivedTick = 50,
                    pendingInputSlackMs = 12,
                    hasPendingInputSlack = true
                };
                var queues = GetField<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager), server, "_clientTicks");
                queues.Add(sender, queue);

                var payload = BitPackerPool.Get();
                payload.WriteBits(1, 1);
                payload.ResetMode(true);
                Deliver(server, firstTick, tickCount, payload, sender, 80);

                Assert.That(queue.Count, Is.Zero);
                Assert.That(queue.ackedServerTick, Is.EqualTo(10UL));
                Assert.That(queue.rawHighestReceivedTick, Is.EqualTo(50UL));
                Assert.That(queue.highestReceivedTick, Is.EqualTo(50UL));
                Assert.That(queue.pendingInputSlackMs, Is.EqualTo(12d));
                Assert.That(queue.hasPendingInputSlack, Is.True);
                Assert.That(payload.positionInBits, Is.Zero,
                    "a rejected upload must still return its payload to the pool");

                var unknownSender = new PlayerID(10, false);
                Deliver(server, firstTick, tickCount, BitPackerPool.Get(true), unknownSender, 80);
                Assert.That(queues.ContainsKey(unknownSender), Is.False,
                    "an invalid upload must not create an input queue");
            }
            finally
            {
                Object.DestroyImmediate(serverObject);
            }
        }

        [Test]
        public void ZeroCountUploadStillAcknowledgesServerFrames()
        {
            var serverObject = new GameObject("ACK-only upload server");
            try
            {
                var server = CreateManager(serverObject);
                SetLocalTick(server, 100);
                var sender = new PlayerID(9, false);
                Deliver(server, ulong.MaxValue, 0, BitPackerPool.Get(true), sender, 80);

                var queue = GetQueue(server, sender);
                Assert.That(queue.ackedServerTick, Is.EqualTo(80UL));
                Assert.That(queue.rawHighestReceivedTick, Is.Zero);
                Assert.That(queue.Count, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(serverObject);
            }
        }

        [TestCase(100UL, 133UL, 32U, 32)]
        [TestCase(100UL, 100UL, 1U, 1)]
        [TestCase(100UL, 164UL, 1U, 1)]
        [TestCase(100UL, 165UL, 1U, 0)]
        [TestCase(100UL, 90UL, 32U, 22)]
        [TestCase(100UL, 68UL, 32U, 0)]
        [TestCase(ulong.MaxValue - 31, ulong.MaxValue - 31, 32U, 32)]
        public void BoundedUploadKeepsTheExistingTickWindow(
            ulong serverTick, ulong firstTick, uint tickCount, int acceptedCount)
        {
            var serverObject = new GameObject("Bounded upload server");
            PredictionManager.InputQueue queue = null;
            try
            {
                var server = CreateManager(serverObject);
                SetLocalTick(server, serverTick);
                var payload = BitPackerPool.Get();
                for (uint i = 0; i < tickCount; i++)
                {
                    payload.WriteBits(0, PredictionManager.ViewOffsetBits);
                    WriteExplicitRelayOffset(payload, 0);
                    Packer<PackedUInt>.Write(payload, 0U);
                    Packer<PackedUInt>.Write(payload, 0U);
                }
                payload.ResetPositionAndMode(true);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, payload, sender, 80);

                queue = GetQueue(server, sender);
                ulong newestTick = firstTick + (tickCount - 1);
                Assert.That(queue.Count, Is.EqualTo(acceptedCount));
                Assert.That(queue.rawHighestReceivedTick, Is.EqualTo(newestTick));
                Assert.That(queue.highestReceivedTick,
                    Is.EqualTo(acceptedCount > 0 ? newestTick : 0UL));
                Assert.That(queue.ackedServerTick, Is.EqualTo(80UL));
            }
            finally
            {
                queue?.Clear();
                Object.DestroyImmediate(serverObject);
            }
        }

        [Test]
        public void ConsumedUploadPrefixStillProvidesTheBaselineForRepeatRecords()
        {
            var clientObject = new GameObject("Consumed prefix upload client");
            var serverObject = new GameObject("Consumed prefix upload server");
            var probeObject = new GameObject("Consumed prefix upload probe");
            var queue = new PredictionManager.InputQueue { lastConsumedTick = 39 };
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);
                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(700), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(probe, typeof(PredictedIdentity<TrackedInput, EmptyState>), 30, 40, _ => 7);
                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 40);
                var sender = new PlayerID(9, false);
                GetField<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager), server, "_clientTicks").Add(sender, queue);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                Assert.That(queue.Count, Is.EqualTo(1));
                var decoded = DecodeSlice(queue.byTick[40]);
                Assert.That(decoded.Count, Is.EqualTo(1));
                Assert.That(decoded[0].id, Is.EqualTo(probeId));
                Assert.That(decoded[0].hasInput, Is.True);
                Assert.That(decoded[0].value, Is.EqualTo(7));
            }
            finally
            {
                queue.Clear();
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void IdenticalInputsOfOneTickReferToTheEarlierEntryAndStillExpandForEachIdentity()
        {
            var clientObject = new GameObject("Shared upload client");
            var serverObject = new GameObject("Shared upload server");
            var firstObject = new GameObject("Shared upload first");
            var secondObject = new GameObject("Shared upload second");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var first = firstObject.AddComponent<StatefulInputProbe>();
                var second = secondObject.AddComponent<StatefulInputProbe>();
                var firstId = new PredictedComponentID(new PredictedObjectID(702), 0);
                var secondId = new PredictedComponentID(new PredictedObjectID(703), 0);
                AttachIdentity(first, client, firstId);
                AttachIdentity(second, client, secondId);
                foreach (var probe in new[] { first, second })
                    SeedInputs(probe, typeof(PredictedIdentity<TrackedInput, EmptyState>), 30, 40, tick => (int)tick);

                FinalizeInput(client, first, second);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < parsed.Count; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(2));
                    Assert.That(parsed[i].entries[0].same, Is.False);
                    Assert.That(parsed[i].entries[1].same, Is.True,
                        $"tick block {i}: the second identical input must refer to the first instead of carrying bits");
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo((int)tickCount));
                for (ulong tick = firstTick; tick < firstTick + tickCount; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(2));
                    Assert.That(decoded[0].id, Is.EqualTo(firstId));
                    Assert.That(decoded[1].id, Is.EqualTo(secondId));
                    Assert.That(decoded[0].value, Is.EqualTo((int)tick));
                    Assert.That(decoded[1].value, Is.EqualTo((int)tick), $"tick {tick} did not expand the shared input");
                }
            }
            finally
            {
                Object.DestroyImmediate(secondObject);
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void ConstantWindowUsesRepeatBitsAndStillExpandsEveryTick()
        {
            var clientObject = new GameObject("Constant upload client");
            var serverObject = new GameObject("Constant upload server");
            var probeObject = new GameObject("Constant upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(700), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    _ => 7);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL),
                    "the upload window must start at the redundancy cap");
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                Assert.That(parsed[0].entries.Count, Is.EqualTo(1));
                Assert.That(parsed[0].entries[0].id, Is.EqualTo(probeId));
                Assert.That(parsed[0].entries[0].repeat, Is.False);
                for (var i = 1; i < 5; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(1));
                    Assert.That(parsed[i].entries[0].id, Is.EqualTo(probeId));
                    Assert.That(parsed[i].entries[0].repeat, Is.True,
                        "an unchanged input payload must ride the repeat bit");
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                Assert.That(queue.highestReceivedTick, Is.EqualTo(40UL));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].hasInput, Is.True);
                    Assert.That(decoded[0].value, Is.EqualTo(7),
                        $"tick {tick} did not expand the repeated payload");
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void RepeatLookupFollowsIdsAcrossRosterReorderingAndRejectsMissingPreviousIds()
        {
            var serverObject = new GameObject("Reordered input roster server");
            try
            {
                var server = CreateManager(serverObject);
                SetLocalTick(server, 30);
                var a = new PredictedComponentID(new PredictedObjectID(700), 0);
                var b = new PredictedComponentID(new PredictedObjectID(701), 0);
                var c = new PredictedComponentID(new PredictedObjectID(702), 0);
                using var payload = BitPackerPool.Get();
                bool firstTick = true;
                AppendTick((a, false, 7), (b, false, 9), (c, false, 11));
                AppendTick((c, true, 0), (a, true, 0));
                AppendTick((c, true, 0), (b, false, 13));
                AppendTick((a, true, 0)); // A was absent from the immediately preceding tick.
                var sender = new PlayerID(9, false);
                Deliver(server, 36, 4, CopyForRead(payload), sender);
                var queue = GetQueue(server, sender);
                Assert.That(queue.Count, Is.EqualTo(3));
                var reordered = DecodeSlice(queue.byTick[37]);
                Assert.That(reordered[0].id, Is.EqualTo(c));
                Assert.That(reordered[0].value, Is.EqualTo(11));
                Assert.That(reordered[1].id, Is.EqualTo(a));
                Assert.That(reordered[1].value, Is.EqualTo(7));
                var replaced = DecodeSlice(queue.byTick[38]);
                Assert.That(replaced[1].id, Is.EqualTo(b));
                Assert.That(replaced[1].value, Is.EqualTo(13));
                Assert.That(queue.byTick.ContainsKey(39), Is.False);

                void AppendTick(params (PredictedComponentID id, bool repeat, int value)[] entries)
                {
                    using var block = BitPackerPool.Get();
                    if (!firstTick && entries.Length > 0)
                        Packer<bool>.Write(block, false); // roster not kept: ids precede every entry
                    firstTick = false;
                    foreach (var entry in entries)
                    {
                        Packer<PredictedComponentID>.Write(block, entry.id);
                        Packer<bool>.Write(block, entry.repeat);
                        if (entry.repeat) continue;
                        Packer<bool>.Write(block, false); // not shared with an earlier entry of the tick
                        Packer<PackedUInt>.Write(block, 33U);
                        Packer<bool>.Write(block, true);
                        Packer<int>.Write(block, entry.value);
                    }
                    payload.WriteBits(0, PredictionManager.ViewOffsetBits);
                    WriteExplicitRelayOffset(payload, 0);
                    Packer<PackedUInt>.Write(payload, (uint)block.positionInBits);
                    Packer<PackedUInt>.Write(payload, (uint)entries.Length);
                    payload.WriteBitsWithoutConsumingIt(block, block.positionInBits);
                }
            }
            finally { Object.DestroyImmediate(serverObject); }
        }

        [Test]
        public void ChangingInputsResendThenPatchTheirPayloadForEveryTick()
        {
            var clientObject = new GameObject("Changing upload client");
            var serverObject = new GameObject("Changing upload server");
            var probeObject = new GameObject("Changing upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(710), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 100 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL));
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < 5; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(1));
                    Assert.That(parsed[i].entries[0].repeat, Is.False);
                    Assert.That(parsed[i].entries[0].patch, Is.EqualTo(i > 0),
                        "a kept roster patches the changed bytes instead of resending the input");
                    Assert.That(parsed[i].entries[0].payloadBitLength, Is.GreaterThan(0));
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].hasInput, Is.True);
                    Assert.That(decoded[0].value, Is.EqualTo(100 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothIdentityKindsUseTheSameUploadWindowAndReenterWithFullPayload(bool constantIsDeterministic)
        {
            var clientObject = new GameObject("Mixed upload client");
            var serverObject = new GameObject("Mixed upload server");
            var deterministicObject = new GameObject("Mixed upload deterministic probe");
            var statefulObject = new GameObject("Mixed upload stateful probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 20);

                var deterministic = deterministicObject.AddComponent<DeterministicInputProbe>();
                var deterministicId = new PredictedComponentID(new PredictedObjectID(720), 0);
                AttachIdentity(deterministic, client, deterministicId);
                SeedInputs(deterministic, typeof(DeterministicIdentity<TrackedInput, EmptyState>),
                    21, 40, tick => constantIsDeterministic ? 5 : 1000 + (int)tick);

                var stateful = statefulObject.AddComponent<StatefulInputProbe>();
                var statefulId = new PredictedComponentID(new PredictedObjectID(721), 0);
                AttachIdentity(stateful, client, statefulId);
                SeedInputs(stateful, typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    21, 40, tick => constantIsDeterministic ? 1000 + (int)tick : 5);

                PredictedIdentity constant = constantIsDeterministic ? deterministic : stateful;
                var constantType = constantIsDeterministic
                    ? typeof(DeterministicIdentity<TrackedInput, EmptyState>)
                    : typeof(PredictedIdentity<TrackedInput, EmptyState>);
                var constantId = constant.id;
                var changingId = constantIsDeterministic ? statefulId : deterministicId;
                GetField<History<TrackedInput>>(constantType, constant, "_inputHistory").Remove(38);

                FinalizeInput(client, deterministic, stateful);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL),
                    "both identity kinds use the same bounded upload redundancy despite an older ACK");
                Assert.That(tickCount, Is.EqualTo(5U));
                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < 5; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(2));
                    Assert.That(FindEntry(parsed[i], changingId).repeat, Is.False);
                }
                Assert.That(FindEntry(parsed[0], constantId).repeat, Is.False);
                Assert.That(FindEntry(parsed[1], constantId).repeat, Is.True);
                Assert.That(FindEntry(parsed[2], constantId).repeat, Is.False,
                    "the explicit absence at tick 38 differs from the preceding input");
                Assert.That(FindEntry(parsed[3], constantId).repeat, Is.False,
                    "an available input must be sent in full when the previous tick had none");
                Assert.That(FindEntry(parsed[4], constantId).repeat, Is.True);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);
                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                Assert.That(queue.byTick.ContainsKey(35), Is.False,
                    "neither identity may extend upload retention below the common window");
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(2));
                    foreach (var entry in decoded)
                    {
                        if (entry.id.Equals(constantId))
                        {
                            Assert.That(entry.hasInput, Is.EqualTo(tick != 38), $"tick {tick}");
                            if (tick != 38)
                                Assert.That(entry.value, Is.EqualTo(5), $"tick {tick}");
                        }
                        else
                        {
                            Assert.That(entry.id, Is.EqualTo(changingId));
                            Assert.That(entry.hasInput, Is.True);
                            Assert.That(entry.value, Is.EqualTo(1000 + (int)tick));
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(statefulObject);
                Object.DestroyImmediate(deterministicObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        public enum RosterChange { Added, Replaced, Removed }

        // The server keeps the expansion of the newest tick it accepted and decodes the next upload
        // against it, so a tick the client already sent must go out again with the same bits after the
        // client's owned roster changed (a spawn, a despawn, a rolled-back client-only create).
        [TestCase(RosterChange.Added)]
        [TestCase(RosterChange.Replaced)]
        [TestCase(RosterChange.Removed)]
        public void RosterChangeLeavesSentTicksAsTheServerAcceptedThem(RosterChange change)
        {
            var clientObject = new GameObject("Roster change upload client");
            var serverObject = new GameObject("Roster change upload server");
            var keptObject = new GameObject("Roster change kept probe");
            var otherObject = new GameObject("Roster change other probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);
                var inputType = typeof(PredictedIdentity<TrackedInput, EmptyState>);

                var kept = keptObject.AddComponent<StatefulInputProbe>();
                var keptId = new PredictedComponentID(new PredictedObjectID(760), 0);
                AttachIdentity(kept, client, keptId);
                var other = otherObject.AddComponent<StatefulInputProbe>();
                var otherId = new PredictedComponentID(new PredictedObjectID(761), 0);
                AttachIdentity(other, client, otherId);

                // Before: kept (plus other when it is the one going away). After, at tick 41: kept
                // gains other, other replaces kept, or other is gone.
                SeedInputs(kept, inputType, 30, change == RosterChange.Replaced ? 40UL : 41UL, tick => 300 + (int)tick);
                if (change == RosterChange.Removed)
                    SeedInputs(other, inputType, 30, 40, tick => 500 + (int)tick);
                else
                    SeedInputs(other, inputType, 41, 41, tick => 500 + (int)tick);

                var before = change == RosterChange.Removed ? new PredictedIdentity[] { kept, other } : new PredictedIdentity[] { kept };
                var after = change switch
                {
                    RosterChange.Added => new PredictedIdentity[] { kept, other },
                    RosterChange.Replaced => new PredictedIdentity[] { other },
                    _ => new PredictedIdentity[] { kept }
                };

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);

                FinalizeInput(client, before);
                var first = GetCachedPayload(client, out var firstTick, out var tickCount);
                Deliver(server, firstTick, tickCount, CopyForRead(first), sender);
                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.ContainsKey(40), Is.True);

                SetLocalTick(client, 41);
                FinalizeInput(client, after);
                var second = GetCachedPayload(client, out firstTick, out tickCount);
                Deliver(server, firstTick, tickCount, CopyForRead(second), sender);

                Assert.That(queue.byTick.ContainsKey(41), Is.True,
                    "the server dropped the first upload after the roster change");
                var decoded = DecodeSlice(queue.byTick[41]);
                Assert.That(decoded.Count, Is.EqualTo(after.Length));
                for (var i = 0; i < after.Length; i++)
                {
                    Assert.That(decoded[i].id, Is.EqualTo(after[i].id));
                    Assert.That(decoded[i].hasInput, Is.True);
                    Assert.That(decoded[i].value, Is.EqualTo(after[i] == kept ? 341 : 541));
                }
            }
            finally
            {
                Object.DestroyImmediate(otherObject);
                Object.DestroyImmediate(keptObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void RedeliveredUploadLeavesTheServerQueueUntouched()
        {
            var clientObject = new GameObject("Hedge upload client");
            var serverObject = new GameObject("Hedge upload server");
            var probeObject = new GameObject("Hedge upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(730), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 200 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                var snapshot = new Dictionary<ulong, BitPacker>();
                foreach (var pair in queue.byTick)
                    snapshot[pair.Key] = pair.Value.inputPacket;
                var rawHighest = queue.rawHighestReceivedTick;
                var highest = queue.highestReceivedTick;

                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                Assert.That(queue.byTick.Count, Is.EqualTo(5),
                    "a fully duplicate hedge resend must not grow the queue");
                Assert.That(queue.rawHighestReceivedTick, Is.EqualTo(rawHighest));
                Assert.That(queue.highestReceivedTick, Is.EqualTo(highest));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    Assert.That(queue.byTick[tick].inputPacket, Is.SameAs(snapshot[tick]),
                        $"tick {tick} slice was rebuilt by a duplicate packet");
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].value, Is.EqualTo(200 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void CorruptedMiddleTickAbortsExpansionAndKeepsEarlierTicks()
        {
            var clientObject = new GameObject("Corrupt upload client");
            var serverObject = new GameObject("Corrupt upload server");
            var probeObject = new GameObject("Corrupt upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(740), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 300 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                Assert.That(parsed[2].entries[0].repeat, Is.False);

                // Flipping the tick-38 repeat flag makes the parser resolve the entry from
                // the previous tick and skip the payload that is still on the wire, so the
                // consumed size no longer matches the declared blockBits and the packet
                // must be abandoned from that tick onward.
                var corrupted = BitPackerPool.Get();
                corrupted.WriteBitsWithoutConsumingIt(cached, cached.positionInBits);
                corrupted.WriteAt(parsed[2].entries[0].repeatBitPosition, true);
                corrupted.ResetPositionAndMode(true);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, corrupted, sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(2));
                Assert.That(queue.byTick.ContainsKey(36), Is.True);
                Assert.That(queue.byTick.ContainsKey(37), Is.True);
                Assert.That(queue.byTick.ContainsKey(38), Is.False,
                    "the corrupt tick must not be queued");
                Assert.That(queue.byTick.ContainsKey(39), Is.False,
                    "ticks after the corrupt one must not be queued");
                Assert.That(queue.byTick.ContainsKey(40), Is.False,
                    "ticks after the corrupt one must not be queued");

                for (ulong tick = 36; tick <= 37; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].value, Is.EqualTo(300 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        private sealed class ParsedUploadEntry
        {
            public PredictedComponentID id;
            public bool repeat;
            public bool same;
            public bool patch;
            public int repeatBitPosition;
            public int payloadBitLength;
        }

        [Test]
        public void ViewOffsetRidesEveryUploadTickAndIsClampedOnTheServer()
        {
            var clientObject = new GameObject("View offset upload client");
            var serverObject = new GameObject("View offset upload server");
            var probeObject = new GameObject("View offset upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(720), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    _ => 7);

                for (ulong tick = 36; tick <= 40; tick++)
                    client.RecordViewOffset(default, tick, (uint)(tick - 36) * 100);
                // Relay offsets repeat on two ticks, so both wire forms are exercised.
                uint[] relaySent = { 80, 80, 81, 4095, 4095 };
                for (ulong tick = 36; tick <= 40; tick++)
                    client.RecordLocalRelayOffset(tick, relaySent[tick - 36]);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL));
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < parsed.Count; i++)
                {
                    Assert.That(parsed[i].viewOffset, Is.EqualTo((uint)i * 100),
                        $"tick block {i} must carry the offset recorded for its tick");
                    Assert.That(parsed[i].relayOffset, Is.EqualTo(relaySent[i]),
                        $"tick block {i} must carry the relay offset recorded for its tick");
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                uint cap = PredictionManager.QuantizeViewOffset(server.maxLagCompensationTicks);
                Assert.That(cap, Is.EqualTo(192u));

                var queue = GetQueue(server, sender);
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    uint sent = (uint)(tick - 36) * 100;
                    Assert.That(queue.byTick[tick].viewOffset, Is.EqualTo(Math.Min(sent, cap)),
                        $"tick {tick} must keep the sent offset up to the server cap");
                    Assert.That(queue.byTick[tick].relayOffset, Is.EqualTo(relaySent[tick - 36]),
                        $"tick {tick} must keep the sent relay offset");
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        // A relay offset that does not repeat the previous tick's: a clear repeat bit, then the value.
        private static void WriteExplicitRelayOffset(BitPacker payload, uint quantized)
        {
            payload.WriteBits(0, 1);
            payload.WriteBits(quantized, PredictionManager.RelayOffsetBits);
        }

        private sealed class ParsedUploadTick
        {
            public uint viewOffset;
            public uint relayOffset;
            public uint declaredBlockBits;
            public readonly List<ParsedUploadEntry> entries = new List<ParsedUploadEntry>();
        }

        private readonly struct DecodedInputEntry
        {
            public readonly PredictedComponentID id;
            public readonly bool hasInput;
            public readonly int value;

            public DecodedInputEntry(PredictedComponentID id, bool hasInput, int value)
            {
                this.id = id;
                this.hasInput = hasInput;
                this.value = value;
            }
        }

        private static PredictionManager CreateManager(GameObject managerObject)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(
                typeof(PredictionManager),
                manager,
                "<tickRate>k__BackingField",
                20);
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

        private static void SetInputAckTick(PredictionManager manager, ulong tick)
        {
            SetField(typeof(PredictionManager), manager, "_inputAckTick", tick);
        }

        private static void AttachIdentity(
            PredictedIdentity identity,
            PredictionManager manager,
            PredictedComponentID id)
        {
            identity.id = id;
            SetField(
                typeof(PredictedIdentity),
                identity,
                "<predictionManager>k__BackingField",
                manager);
        }

        private static void SeedInputs(
            Component identity,
            Type declaringType,
            ulong fromTick,
            ulong toTick,
            Func<ulong, int> value)
        {
            var history = new History<TrackedInput>(200);
            for (var tick = fromTick; tick <= toTick; tick++)
                history.Write(tick, new TrackedInput(value(tick)));
            SetField(declaringType, identity, "_inputHistory", history);
        }

        private static void FinalizeInput(
            PredictionManager client,
            params PredictedIdentity[] owned)
        {
            var method = typeof(PredictionManager).GetMethod(
                "FinalizeInputOnClient",
                InstanceFields);
            Assert.That(method, Is.Not.Null);

            var list = DisposableList<PredictedIdentity>.Create(owned.Length);
            try
            {
                for (var i = 0; i < owned.Length; i++)
                    list.Add(owned[i]);

                // The RPC send at the end of FinalizeInputOnClient is a no-op for an
                // unspawned identity on an unreliable channel; the payload cache is
                // written before the send either way.
                try
                {
                    method.Invoke(client, new object[] { list });
                }
                catch (TargetInvocationException)
                {
                }
            }
            finally
            {
                list.Dispose();
            }
        }

        private static BitPacker GetCachedPayload(
            PredictionManager client,
            out ulong firstTick,
            out uint tickCount)
        {
            var payload = GetField<BitPacker>(
                typeof(PredictionManager),
                client,
                "_cachedInputPayload");
            Assert.That(payload, Is.Not.Null,
                "FinalizeInputOnClient must cache the payload before sending");
            firstTick = GetField<ulong>(
                typeof(PredictionManager),
                client,
                "_cachedInputFirstTick");
            tickCount = GetField<uint>(
                typeof(PredictionManager),
                client,
                "_cachedInputTickCount");
            return payload;
        }

        private static BitPacker CopyForRead(BitPacker source)
        {
            var copy = BitPackerPool.Get();
            copy.WriteBitsWithoutConsumingIt(source, source.positionInBits);
            copy.ResetPositionAndMode(true);
            return copy;
        }

        private static void Deliver(
            PredictionManager server,
            ulong firstTick,
            uint tickCount,
            BitPacker payload,
            PlayerID sender,
            ulong frameAck = 0)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ReceivedInput",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            var info = new RPCInfo { sender = sender };
            method.Invoke(server, new object[] { firstTick, tickCount, frameAck, payload, info });
        }

        private static PredictionManager.InputQueue GetQueue(
            PredictionManager server,
            PlayerID sender)
        {
            var clientTicks =
                GetField<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager),
                    server,
                    "_clientTicks");
            Assert.That(clientTicks.TryGetValue(sender, out var queue), Is.True,
                "the server never registered the sender's input queue");
            return queue;
        }

        private static List<ParsedUploadTick> ParseUpload(
            BitPacker cachedPayload,
            uint tickCount)
        {
            var payload = CopyForRead(cachedPayload);
            try
            {
                var result = new List<ParsedUploadTick>();
                ParsedUploadTick previous = null;
                uint relayOffset = 0;
                for (uint i = 0; i < tickCount; i++)
                {
                    uint viewOffset = (uint)payload.ReadBits((byte)PredictionManager.ViewOffsetBits);
                    PredictionManager.ReadRelayOffset(payload, ref relayOffset, i == 0);
                    PackedUInt blockBits = default;
                    Packer<PackedUInt>.Read(payload, ref blockBits);
                    PackedUInt entryCount = default;
                    Packer<PackedUInt>.Read(payload, ref entryCount);
                    int blockStart = payload.positionInBits;

                    var tickBlock = new ParsedUploadTick
                    {
                        viewOffset = viewOffset,
                        relayOffset = relayOffset,
                        declaredBlockBits = blockBits.value
                    };

                    // Later ticks state whether they keep the previous tick's roster; a kept
                    // roster omits ids and may patch an input against its previous payload.
                    bool keptRoster = i > 0 && entryCount.value > 0 && Packer<bool>.Read(payload);
                    for (uint e = 0; e < entryCount.value; e++)
                    {
                        PredictedComponentID pid = default;
                        if (keptRoster)
                            pid = previous.entries[(int)e].id;
                        else Packer<PredictedComponentID>.Read(payload, ref pid);
                        int repeatBitPosition = payload.positionInBits;
                        bool repeat = Packer<bool>.Read(payload);
                        bool same = !repeat && Packer<bool>.Read(payload);
                        bool patch = keptRoster && !repeat && !same && Packer<bool>.Read(payload);
                        int payloadBitLength = 0;
                        if (same)
                        {
                            uint distance = Packer<PackedUInt>.Read(payload);
                            Assert.That(distance, Is.LessThan(e), "a shared input refers to an earlier entry of its tick");
                            payloadBitLength = tickBlock.entries[(int)(e - distance - 1)].payloadBitLength;
                        }
                        else if (patch)
                        {
                            payloadBitLength = previous.entries[(int)e].payloadBitLength;
                            int bytes = (payloadBitLength - 1) / 8 + 1;
                            for (int first = 0; first < bytes; first += 8)
                            {
                                int mask = (int)payload.ReadBits(8);
                                for (int b = 0; b < Math.Min(8, bytes - first); b++)
                                    if ((mask & (1 << b)) != 0)
                                        payload.SkipBits(8);
                            }
                        }
                        else if (!repeat)
                        {
                            PackedUInt bits = default;
                            Packer<PackedUInt>.Read(payload, ref bits);
                            payloadBitLength = checked((int)bits.value);
                            payload.SkipBits(payloadBitLength);
                        }
                        else if (previous != null)
                        {
                            foreach (var earlier in previous.entries)
                                if (earlier.id.Equals(pid))
                                    payloadBitLength = earlier.payloadBitLength;
                        }

                        tickBlock.entries.Add(new ParsedUploadEntry
                        {
                            id = pid,
                            repeat = repeat,
                            same = same,
                            patch = patch,
                            repeatBitPosition = repeatBitPosition,
                            payloadBitLength = payloadBitLength
                        });
                    }

                    Assert.That(
                        payload.positionInBits - blockStart,
                        Is.EqualTo((int)blockBits.value),
                        $"tick block {i} declared size does not match its contents");
                    result.Add(tickBlock);
                    previous = tickBlock;
                }

                return result;
            }
            finally
            {
                payload.Dispose();
            }
        }

        private static ParsedUploadEntry FindEntry(
            ParsedUploadTick tickBlock,
            PredictedComponentID id)
        {
            for (var i = 0; i < tickBlock.entries.Count; i++)
            {
                if (tickBlock.entries[i].id.Equals(id))
                    return tickBlock.entries[i];
            }

            Assert.Fail($"No entry for {id} in the tick block");
            return null;
        }

        private static List<DecodedInputEntry> DecodeSlice(
            PredictionManager.InputQueueValue entry)
        {
            var packet = entry.inputPacket;
            var result = new List<DecodedInputEntry>();
            Assert.That(entry.spans.Count, Is.EqualTo((int)entry.count.value));
            foreach (var span in entry.spans)
            {
                var pid = span.id;
                packet.SetBitPosition(span.bitOrigin);
                bool hasInput = Packer<bool>.Read(packet);
                int value = 0;
                if (hasInput)
                    value = Packer<TrackedInput>.Read(packet).id;
                result.Add(new DecodedInputEntry(pid, hasInput, value));
            }

            return result;
        }

        private static T GetField<T>(
            Type declaringType,
            object target,
            string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
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
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }
}
