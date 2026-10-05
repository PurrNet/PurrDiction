using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InputSectionRoundTripTests
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

        [Test]
        public void LaggingBaselineIncludesOrdinaryAndDeterministicInputsAtEveryTick()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var deterministic = sender.AddInput(true, 600);
            var ordinary = sender.AddInput(false, 601);
            var receivedDeterministic = receiver.AddInput(true, 600);
            var receivedOrdinary = receiver.AddInput(false, 601);
            for (ulong tick = 13; tick <= 20; tick++)
            {
                deterministic.Write(tick, new TrackedInput((int)(100 + tick)));
                ordinary.Write(tick, new TrackedInput(tick is 16 or 20 ? 555 : (int)(200 + tick)));
                sender.Capture(tick);
            }

            using var frameA = sender.Write(16, 13);
            receiver.Read(frameA, 16, 13);
            for (ulong tick = 14; tick <= 16; tick++)
            {
                AssertInput(receivedDeterministic, tick, (int)(100 + tick));
                AssertInput(receivedOrdinary, tick, tick == 16 ? 555 : (int)(200 + tick));
            }
            Assert.That(receivedDeterministic.TryGet(13, out _), Is.False);
            Assert.That(receivedOrdinary.TryGet(13, out _), Is.False,
                "the acknowledged baseline itself must not be included");

            using var frameB = sender.Write(20, 16);
            var writtenBits = frameB.positionInBits;
            frameB.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frameB), Is.EqualTo(4));
            for (ulong tick = 17; tick <= 20; tick++)
            {
                Assert.That(ReadPackedUInt(frameB), Is.EqualTo(2));
                Assert.That(ReadTickHeader(frameB, tick > 17, 2, true, out var deltas), Is.All.False,
                    "changing inputs are never encoded as repeats");
                AssertRecord(frameB, 600, (int)(100 + tick), tick > 17, deltas[0], (int)(99 + tick));
                AssertRecord(frameB, 601, tick == 20 ? 555 : (int)(200 + tick), tick > 17, deltas[1], (int)(199 + tick));
            }
            Assert.That(frameB.positionInBits, Is.EqualTo(writtenBits),
                "the transcript must be the complete input section, with no newest-only trailer");
            receiver.Read(frameB, 20, 16);
            for (ulong tick = 17; tick <= 20; tick++)
            {
                AssertInput(receivedDeterministic, tick, (int)(100 + tick));
                AssertInput(receivedOrdinary, tick, tick == 20 ? 555 : (int)(200 + tick));
            }
        }

        [Test]
        public void RepeatedDecodeReplacesEveryTickWithoutDuplicatingEntries()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var sent = new[] { sender.AddInput(false, 610), sender.AddInput(true, 611) };
            var received = new[] { receiver.AddInput(false, 610), receiver.AddInput(true, 611) };
            for (ulong tick = 12; tick <= 16; tick++)
            {
                foreach (var history in sent)
                    history.Write(tick, new TrackedInput((int)tick));
                sender.Capture(tick);
            }

            using var frame = sender.Write(16, 12);
            for (var pass = 0; pass < 2; pass++)
            {
                receiver.Read(frame, 16, 12);
                foreach (var history in received)
                {
                    Assert.That(history.Count, Is.EqualTo(4), $"pass {pass}");
                    for (ulong tick = 13; tick <= 16; tick++)
                        AssertInput(history, tick, (int)tick);
                }
            }
        }

        [Test]
        public void HistoricalVisibilityFiltersEachTickInsteadOfUsingLatestVisibility()
        {
            using var sender = new InputTranscriptFixture("sender");
            sender.EnableVisibility();
            var sent = new[] { sender.AddInput(false, 620), sender.AddInput(true, 621) };
            for (ulong tick = 11; tick <= 15; tick++)
            {
                foreach (var history in sent)
                    history.Write(tick, new TrackedInput((int)tick));
                sender.Capture(tick);
            }

            var timeline = new PlayerVisibilityTimeline();
            timeline.SetVisible(12, new PredictedObjectID(620), false);
            timeline.SetVisible(13, new PredictedObjectID(621), false);
            timeline.SetVisible(14, new PredictedObjectID(620), true);
            using var frame = sender.Write(15, 10, timeline);
            var end = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(5));
            for (ulong tick = 11; tick <= 15; tick++)
            {
                bool ordinaryVisible = tick == 11 || tick >= 14;
                bool deterministicVisible = tick <= 12;
                var count = (ordinaryVisible ? 1 : 0) + (deterministicVisible ? 1 : 0);
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(count), $"tick {tick}");
                // Only tick 15 sees the same roster as its predecessor (620 alone at 14 and 15).
                Assert.That(ReadTickHeader(frame, tick > 11, count, tick == 15, out var deltas), Is.All.False);
                if (ordinaryVisible)
                    AssertRecord(frame, 620, (int)tick, tick == 15, deltas[0], (int)tick - 1);
                if (deterministicVisible)
                    AssertRecord(frame, 621, (int)tick);
            }
            Assert.That(frame.positionInBits, Is.EqualTo(end));
        }

        [Test]
        public void UnknownIdentityIsFramedSafelyButCannotBecomeVerified()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var unknown = sender.AddInput(false, 630);
            var known = sender.AddInput(true, 631);
            receiver.AddInput(true, 631);
            for (ulong tick = 11; tick <= 14; tick++)
            {
                unknown.Write(tick, new TrackedInput((int)(100 + tick)));
                known.Write(tick, new TrackedInput((int)tick));
                sender.Capture(tick);
            }
            using var frame = sender.Write(14, 10);
            var end = frame.positionInBits;
            Packer<int>.Write(frame, 0x12345678);
            frame.ResetPositionAndMode(true);
            ReadInputHistory(receiver.manager, frame, 14, 10, end);
            Assert.That(frame.positionInBits, Is.EqualTo(end));
            Assert.That(Packer<int>.Read(frame), Is.EqualTo(0x12345678));
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Apply(11),
                "an unresolved authoritative identity cannot be silently skipped during verified simulation");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingAuthoritativeInputCannotBeSatisfiedByASpeculativeLocalEntry(bool explicitAbsent)
        {
            using var receiver = new InputTranscriptFixture("receiver");
            var received = receiver.AddInput(false, 635);
            received.Write(11, new TrackedInput(999));
            using var frame = BitPackerPool.Get();
            Packer<PackedUInt>.Write(frame, 1U);
            Packer<PackedUInt>.Write(frame, explicitAbsent ? 1U : 0U);
            if (explicitAbsent)
            {
                Packer<PackedUInt>.Write(frame, 0U);
                PadToByte(frame);
                Packer<PredictedComponentID>.Write(frame,
                    new PredictedComponentID(new PredictedObjectID(635), 0));
                Packer<bool>.Write(frame, false);
                Packer<PackedUInt>.Write(frame, 1U);
                Packer<bool>.Write(frame, false);
            }
            receiver.Parse(frame, 11, 10);
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Apply(11),
                "a predicted local input must not masquerade as a verified server input");
        }

        [Test]
        public void StagedInputsResolveIdentitiesAtTheirSimulationTickAndSurviveHistoryReset()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var existing = sender.AddInput(false, 636);
            History<TrackedInput> spawned = null;
            var receivedExisting = receiver.AddInput(false, 636);
            for (ulong tick = 11; tick <= 14; tick++)
            {
                existing.Write(tick, new TrackedInput((int)tick));
                if (tick == 13)
                    spawned = sender.AddInput(true, 637);
                if (tick >= 13)
                    spawned.Write(tick, new TrackedInput((int)(100 + tick)));
                sender.Capture(tick);
            }
            using var frame = sender.Write(14, 10);
            receiver.Parse(frame, 14, 10);
            Assert.That(receivedExisting.Count, Is.Zero,
                "parsing must preserve the transcript until rollback and hierarchy restoration finish");

            receiver.Apply(11);
            AssertInput(receivedExisting, 11, 11);
            receivedExisting.Clear();
            receiver.Apply(11);
            AssertInput(receivedExisting, 11, 11);
            receiver.Apply(12);

            // Replaying earlier ticks may create an input identity that did not exist
            // when the later frame was parsed. Resolve its addressed inputs now.
            var receivedSpawned = receiver.AddInput(true, 637);
            for (ulong tick = 13; tick <= 14; tick++)
            {
                receiver.Apply(tick);
                AssertInput(receivedExisting, tick, (int)tick);
                AssertInput(receivedSpawned, tick, (int)(100 + tick));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateOrTruncatedRecordsCannotBecomeAVerifiedTranscript(bool duplicate)
        {
            using var receiver = new InputTranscriptFixture("receiver");
            receiver.AddInput(false, 638);
            using var payload = BitPackerPool.Get();
            Packer<bool>.Write(payload, true);
            Packer<TrackedInput>.Write(payload, new TrackedInput(11));
            using var frame = BitPackerPool.Get();
            Packer<PackedUInt>.Write(frame, 1U);
            Packer<PackedUInt>.Write(frame, duplicate ? 2U : 1U);
            Packer<PackedUInt>.Write(frame, 0U);
            PadToByte(frame);
            for (var record = 0; record < (duplicate ? 2 : 1); record++)
            {
                Packer<PredictedComponentID>.Write(frame,
                    new PredictedComponentID(new PredictedObjectID(638), 0));
                Packer<PackedUInt>.Write(frame,
                    (uint)(payload.positionInBits + (duplicate ? 0 : 1)));
                frame.WriteBitsWithoutConsumingIt(payload, payload.positionInBits);
            }
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Parse(frame, 11, 10));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LaterFrameRecoversPlayerRosterEventsForDeterministicSubscribers(bool includeRemoval)
        {
            var senderManagerObject = new GameObject("Roster gap sender manager");
            var receiverManagerObject = new GameObject("Roster gap receiver manager");
            var senderObject = new GameObject("Roster gap sender");
            var receiverObject = new GameObject("Roster gap receiver");
            var consumerObject = new GameObject("Roster gap deterministic consumer");
            PredictedPlayers sender = null;
            PredictedPlayers receiver = null;
            PlayerRosterTranscriptConsumer consumer = null;
            try
            {
                var senderManager = CreateManager(senderManagerObject);
                var receiverManager = CreateManager(receiverManagerObject);
                var rosterId = new PredictedComponentID(new PredictedObjectID(625), 0);
                var initial = new PlayerID(new PackedULong(10), false);
                var first = new PlayerID(new PackedULong(21), false);
                var second = new PlayerID(new PackedULong(22), false);
                sender = AddPlayerRoster(senderObject, senderManager, rosterId, initial, out var sentInputs);
                receiver = AddPlayerRoster(receiverObject, receiverManager, rosterId, initial, out var receivedInputs);
                consumer = consumerObject.AddComponent<PlayerRosterTranscriptConsumer>();
                SetField(typeof(PredictedIdentity), consumer, "<predictionManager>k__BackingField", receiverManager);
                SetField(typeof(DeterministicIdentity<PredictedPlayersState>), consumer, "_stateHistory",
                    new History<FULL_STATE<PredictedPlayersState>>(200));
                consumer.currentState = RosterWith(initial);
                consumer.fullPredictedState.prediction.wasOnSimulationStartCalled = true;

                var events = new List<string>();
                ulong simulatedTick = 0;
                receiver.onPlayerAdded += player =>
                {
                    events.Add($"add:{simulatedTick}:{player.id.value}");
                    consumer.AddPlayer(player);
                };
                receiver.onPlayerRemoved += player =>
                {
                    events.Add($"remove:{simulatedTick}:{player.id.value}");
                    consumer.RemovePlayer(player);
                };

                // The frame carrying each one-shot event is sent but never reaches this peer.
                // The later frame is still relative to its last acknowledged tick, 10.
                for (ulong tick = 11; tick <= 13; tick++)
                {
                    SetLocalTick(senderManager, tick);
                    sentInputs.Write(tick, tick == 11 ? RosterChange(first, true)
                        : tick == 12 ? RosterChange(second, true)
                        : includeRemoval ? RosterChange(first, false) : default);
                    CaptureInputHistory(senderManager, tick);
                    using var lostFrame = BitPackerPool.Get();
                    WriteVisibilityInputHistory(senderManager, lostFrame, baselineTick: 10);
                }
                Assert.That(receivedInputs.Count, Is.Zero);

                SetLocalTick(senderManager, 14);
                sentInputs.Write(14, default);
                CaptureInputHistory(senderManager, 14);
                using var recoveredFrame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, recoveredFrame, baselineTick: 10);
                int writtenBits = recoveredFrame.positionInBits;
                // RollbackToFrame can decode twice around hierarchy materialization. Reading
                // the same transcript must replace input entries and never dispatch events.
                for (var read = 0; read < 2; read++)
                {
                    recoveredFrame.ResetPositionAndMode(true);
                    ReadInputHistory(receiverManager, recoveredFrame, serverTick: 14, baselineTick: 10,
                        frameEndBit: writtenBits);
                    Assert.That(recoveredFrame.positionInBits, Is.EqualTo(writtenBits));
                    Assert.That(events, Is.Empty);
                }

                var expectedEvents = new List<string> { "add:11:21", "add:12:22" };
                if (includeRemoval)
                    expectedEvents.Add("remove:13:21");

                for (var replay = 0; replay < 2; replay++)
                {
                    if (replay > 0)
                    {
                        receiver.RunClearFuture(11);
                        consumer.RunClearFuture(11);
                        receiver.RunRollback(11);
                        consumer.RunRollback(11);
                        events.Clear();
                    }

                    for (ulong tick = 11; tick <= 14; tick++)
                    {
                        simulatedTick = tick;
                        receiver.RunSaveState(tick);
                        consumer.RunSaveState(tick);
                        var applyInputs = typeof(PredictionManager).GetMethod("ApplyVerifiedInputs", InstanceFields);
                        Assert.That(applyInputs, Is.Not.Null);
                        applyInputs.Invoke(receiverManager, new object[] { tick });
                        receiver.RunPrepareSimulationInputs(tick, 1f / 20);
                        receiver.RunSimulateTick(tick, 1f / 20);
                        consumer.RunSimulateTick(tick, 1f / 20);
                        Assert.That(consumer.currentState.players,
                            Is.EqualTo(receiver.currentState.players), $"membership at replay {replay}, tick {tick}");
                    }

                    Assert.That(events, Is.EqualTo(expectedEvents),
                        "events must occur at their original simulation ticks, including during rollback replay");
                    Assert.That(consumer.currentState.players.Contains(initial), Is.True);
                    Assert.That(consumer.currentState.players.Contains(second), Is.True);
                    Assert.That(consumer.currentState.players.Contains(first), Is.EqualTo(!includeRemoval));
                    Assert.That(consumer.currentState.players.Count, Is.EqualTo(includeRemoval ? 2 : 3));
                }
            }
            finally
            {
                // Seeded Editor fixtures do not reliably receive OnDestroy; use the real
                // production release path so retained disposable inputs/states are reclaimed.
                consumer?.ReleasePredictionStateForPool();
                receiver?.ReleasePredictionStateForPool();
                sender?.ReleasePredictionStateForPool();
                Object.DestroyImmediate(consumerObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                InputTranscriptFixture.DisposeManagerCaches(receiverManagerObject.GetComponent<PredictionManager>());
                InputTranscriptFixture.DisposeManagerCaches(senderManagerObject.GetComponent<PredictionManager>());
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        private static PredictedPlayers AddPlayerRoster(
            GameObject gameObject, PredictionManager manager, PredictedComponentID id,
            PlayerID initial, out History<PredictedPlayersInput> inputHistory)
        {
            var players = gameObject.AddComponent<PredictedPlayers>();
            players.id = id;
            players.extrapolateInput = false;
            SetField(typeof(PredictedIdentity), players, "<predictionManager>k__BackingField", manager);
            inputHistory = new History<PredictedPlayersInput>(200);
            SetField(typeof(PredictedIdentity<PredictedPlayersInput, PredictedPlayersState>),
                players, "_inputHistory", inputHistory);
            SetField(typeof(PredictedIdentity<PredictedPlayersState>), players, "_stateHistory",
                new History<FULL_STATE<PredictedPlayersState>>(200));
            SetField(typeof(PredictedIdentity<PredictedPlayersState>), players, "myType", typeof(PredictedPlayers));
            players.currentState = RosterWith(initial);
            players.fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            RegisterSystem(manager, players);
            return players;
        }

        private static PredictedPlayersState RosterWith(PlayerID player)
        {
            var state = new PredictedPlayersState { players = DisposableList<PlayerID>.Create(4) };
            state.players.Add(player);
            return state;
        }

        private static PredictedPlayersInput RosterChange(PlayerID player, bool add)
        {
            var values = DisposableList<PlayerID>.Create(1);
            values.Add(player);
            return add
                ? new PredictedPlayersInput { addPlayers = values }
                : new PredictedPlayersInput { removePlayers = values };
        }

        [Test]
        public void EmptyTicksKeepTheCompleteDeclaredInterval()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            for (ulong tick = 18; tick <= 20; tick++)
                sender.Capture(tick);
            using var frame = sender.Write(20, 17);
            var writtenBits = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(3));
            for (var tick = 0; tick < 3; tick++)
                Assert.That(ReadPackedUInt(frame), Is.Zero);
            Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
            receiver.Read(frame, 20, 17);

            using var noAdvance = sender.Write(20, 20);
            noAdvance.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(noAdvance), Is.Zero);
            receiver.Read(noAdvance, 20, 20);
        }

        [TestCase(20)]
        [TestCase(60)]
        public void EntireRetainedWindowRoundTripsIncludingItsOldestTick(int tickRate)
        {
            using var sender = new InputTranscriptFixture("sender", tickRate);
            using var receiver = new InputTranscriptFixture("receiver", tickRate);
            var sent = sender.AddInput(false, 640);
            var received = receiver.AddInput(false, 640);
            ulong end = 10 + sender.manager.verifiedHistoryWindowTicks;
            for (ulong tick = 11; tick <= end; tick++)
            {
                sent.Write(tick, new TrackedInput((int)tick));
                sender.Capture(tick);
            }
            using var frame = sender.Write(end, 10);
            receiver.Read(frame, end, 10);
            Assert.That(received.Count, Is.EqualTo((int)sender.manager.verifiedHistoryWindowTicks));
            for (ulong tick = 11; tick <= end; tick++)
                AssertInput(received, tick, (int)tick);
        }

        [Test]
        public void UnchangedInputsAreEncodedAsOneBitRepeatsAndRoundTrip()
        {
            using var sender = new InputTranscriptFixture("sender");
            using var receiver = new InputTranscriptFixture("receiver");
            var sent = sender.AddInput(false, 660);
            var received = receiver.AddInput(false, 660);
            for (ulong tick = 11; tick <= 20; tick++)
            {
                sent.Write(tick, new TrackedInput(tick <= 15 ? 7 : ~7));
                sender.Capture(tick);
            }

            using var frame = sender.Write(20, 10);
            int writtenBits = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(10));
            for (ulong tick = 11; tick <= 20; tick++)
            {
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(1), $"tick {tick}");
                bool repeat = ReadTickHeader(frame, tick > 11, 1, tick > 11, out var deltas)[0];
                Assert.That(repeat, Is.EqualTo(tick != 11 && tick != 16),
                    $"tick {tick}: unchanged values repeat; changed values carry a payload");
                if (repeat)
                    continue;
                if (tick == 16)
                    Assert.That(deltas[0], Is.False, "a change touching every byte is written as an id-less full record");
                AssertRecord(frame, 660, tick <= 15 ? 7 : ~7, tick > 11, deltas[0], 7);
            }
            Assert.That(frame.positionInBits, Is.EqualTo(writtenBits),
                "transcript size must not scale with the number of unchanged ticks");

            receiver.Read(frame, 20, 10);
            for (ulong tick = 11; tick <= 20; tick++)
                AssertInput(received, tick, tick <= 15 ? 7 : ~7);
        }

        [Test]
        public void TranscriptMustCoverEveryTickAfterTheStateBaseline()
        {
            using var sender = new InputTranscriptFixture("coverage sender");
            using var receiver = new InputTranscriptFixture("coverage receiver");
            var sent = sender.AddInput(false, 670);
            receiver.AddInput(false, 670);
            for (ulong tick = 11; tick <= 20; tick++)
            {
                sent.Write(tick, new TrackedInput((int)tick));
                sender.Capture(tick);
            }

            using var shortened = sender.Write(20, 16);
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Parse(shortened, 20, 10),
                "gap replay needs an authoritative input for every tick after the state baseline");

            using var overlong = sender.Write(20, 10);
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Parse(overlong, 20, 16),
                "a transcript cannot reach further back than the state baseline");
        }

        [Test]
        public void ReceiverOwnedInputsTheServerUsedAsUploadedAreRestoredNotEchoed()
        {
            var owner = new PlayerID(7, false);
            using var sender = new InputTranscriptFixture("restore sender");
            using var receiver = new InputTranscriptFixture("restore receiver");
            sender.AddInput(false, 680);
            var serverIdentity = sender.lastIdentity;
            var received = receiver.AddInput(false, 680);
            SetOwner(serverIdentity, owner);
            SetOwner(receiver.lastIdentity, owner);
            for (ulong tick = 11; tick <= 13; tick++)
            {
                UploadAndPrepare(serverIdentity, owner, tick, (int)tick);
                sender.Capture(tick);
                receiver.RecordUpload(tick, 680, (int)tick);
            }

            using var frame = sender.Write(13, 10, player: owner);
            int writtenBits = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(3));
            for (ulong tick = 11; tick <= 13; tick++)
            {
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(1), $"tick {tick}");
                if (tick > 11)
                    Assert.That(Packer<bool>.Read(frame), Is.True, "same roster");
                Assert.That(ReadPackedUInt(frame), Is.Zero, "view offsets");
                if (tick > 11)
                {
                    Assert.That(Packer<bool>.Read(frame), Is.False, "changing values never repeat");
                    Assert.That(Packer<bool>.Read(frame), Is.False, "uploaded inputs are restored, never patched");
                    Assert.That(Packer<bool>.Read(frame), Is.True, "restore flag");
                }
                frame.SkipBits((8 - frame.positionInBits % 8) % 8);
                if (tick == 11)
                {
                    Assert.That(Packer<PredictedComponentID>.Read(frame),
                        Is.EqualTo(new PredictedComponentID(new PredictedObjectID(680), 0)));
                    Assert.That(Packer<bool>.Read(frame), Is.True, "restore flag");
                }
            }
            Assert.That(frame.positionInBits, Is.EqualTo(writtenBits), "no input payload is echoed to its owner");

            receiver.Read(frame, 13, 10);
            for (ulong tick = 11; tick <= 13; tick++)
                AssertInput(received, tick, (int)tick);
        }

        [Test]
        public void RestoredInputsRequireTheReceiverToStillHoldItsUpload()
        {
            var owner = new PlayerID(7, false);
            using var sender = new InputTranscriptFixture("restore-miss sender");
            using var receiver = new InputTranscriptFixture("restore-miss receiver");
            sender.AddInput(false, 681);
            var serverIdentity = sender.lastIdentity;
            var received = receiver.AddInput(false, 681);
            SetOwner(serverIdentity, owner);
            SetOwner(receiver.lastIdentity, owner);
            for (ulong tick = 11; tick <= 13; tick++)
            {
                UploadAndPrepare(serverIdentity, owner, tick, (int)tick);
                sender.Capture(tick);
                if (tick != 12)
                    receiver.RecordUpload(tick, 681, (int)tick);
                received.Write(tick, new TrackedInput((int)tick));
            }

            using var frame = sender.Write(13, 10, player: owner);
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Parse(frame, 13, 10),
                "an upload this peer no longer holds cannot be restored, even while the identity still has it");
        }

        [Test]
        public void RestoredInputsSurviveTheOwnerBeingDespawnedBeforeTheFrameArrives()
        {
            var owner = new PlayerID(7, false);
            using var sender = new InputTranscriptFixture("despawn sender");
            using var receiver = new InputTranscriptFixture("despawn receiver");
            sender.AddInput(false, 683);
            var serverIdentity = sender.lastIdentity;
            receiver.AddInput(false, 683);
            var clientIdentity = receiver.lastIdentity;
            SetOwner(serverIdentity, owner);
            SetOwner(clientIdentity, owner);
            for (ulong tick = 11; tick <= 13; tick++)
            {
                UploadAndPrepare(serverIdentity, owner, tick, (int)tick);
                sender.Capture(tick);
                receiver.RecordUpload(tick, 683, (int)tick);
            }

            // A non-predicted despawn (round end) destroys the owner's identity on the client
            // before the frame covering its last uploads is applied.
            receiver.Despawn(clientIdentity);

            using var frame = sender.Write(13, 10, player: owner);
            Assert.DoesNotThrow(() => receiver.Parse(frame, 13, 10),
                "restored inputs must not depend on the owning identity still existing");
        }

        [Test]
        public void RestoredInputsSurviveUploadsRunningAheadByThePredictionLead()
        {
            var owner = new PlayerID(7, false);
            using var sender = new InputTranscriptFixture("lead sender");
            using var receiver = new InputTranscriptFixture("lead receiver");
            sender.AddInput(false, 684);
            var serverIdentity = sender.lastIdentity;
            receiver.AddInput(false, 684);
            SetOwner(serverIdentity, owner);
            SetOwner(receiver.lastIdentity, owner);
            for (ulong tick = 11; tick <= 13; tick++)
            {
                UploadAndPrepare(serverIdentity, owner, tick, (int)tick);
                sender.Capture(tick);
            }

            // The client predicts up to the maximum lead past its verified tick, so it has
            // uploaded well beyond the ticks the next frame repeats.
            receiver.SetVerifiedTick(10);
            ulong window = receiver.manager.verifiedHistoryWindowTicks;
            for (ulong tick = 11; tick <= 13 + window + 30; tick++)
                receiver.RecordUpload(tick, 684, (int)tick);

            using var frame = sender.Write(13, 10, player: owner);
            Assert.DoesNotThrow(() => receiver.Parse(frame, 13, 10),
                "an upload the next applicable frame can still repeat must not be pruned by the prediction lead");
        }

        [Test]
        public void UploadsBehindTheVerifiedHistoryWindowAreReleased()
        {
            using var receiver = new InputTranscriptFixture("prune receiver");
            receiver.AddInput(false, 685);
            ulong window = receiver.manager.verifiedHistoryWindowTicks;
            for (ulong tick = 11; tick <= 20; tick++)
                receiver.RecordUpload(tick, 685, (int)tick);

            receiver.SetVerifiedTick(15 + window);
            receiver.RecordUpload(21, 685, 21);

            var held = receiver.UploadedTicks();
            Assert.That(held, Is.EquivalentTo(new ulong[] { 16, 17, 18, 19, 20, 21 }),
                "no applicable frame can repeat a tick at or before the verified tick minus the window");
        }

        [Test]
        public void SubstitutedAndForeignInputsAreStillEchoed()
        {
            var owner = new PlayerID(7, false);
            var other = new PlayerID(9, false);
            using var sender = new InputTranscriptFixture("echo sender");
            using var receiver = new InputTranscriptFixture("echo receiver");
            sender.AddInput(false, 682);
            var serverIdentity = sender.lastIdentity;
            var received = receiver.AddInput(false, 682);
            SetOwner(serverIdentity, owner);
            SetOwner(receiver.lastIdentity, owner);
            for (ulong tick = 11; tick <= 13; tick++)
            {
                // Tick 12 never arrived at the server, which simulated it with the default input.
                UploadAndPrepare(serverIdentity, owner, tick, tick == 12 ? null : (int)tick);
                sender.Capture(tick);
                receiver.RecordUpload(tick, 682, (int)tick);
                received.Write(tick, new TrackedInput((int)tick));
            }

            using var frame = sender.Write(13, 10, player: owner);
            int writtenBits = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(3));
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(1));
            Assert.That(ReadPackedUInt(frame), Is.Zero);
            frame.SkipBits((8 - frame.positionInBits % 8) % 8);
            Assert.That(Packer<PredictedComponentID>.Read(frame),
                Is.EqualTo(new PredictedComponentID(new PredictedObjectID(682), 0)));
            Assert.That(Packer<bool>.Read(frame), Is.True, "tick 11 was uploaded");
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(1));
            Assert.That(Packer<bool>.Read(frame), Is.True, "same roster");
            Assert.That(ReadPackedUInt(frame), Is.Zero);
            Assert.That(Packer<bool>.Read(frame), Is.False, "the substitute differs from tick 11");
            Assert.That(Packer<bool>.Read(frame), Is.True, "the substitute is patched against tick 11");
            frame.SkipBits((8 - frame.positionInBits % 8) % 8);
            AssertRecord(frame, 682, 0, true, true, 11);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(1));
            Assert.That(Packer<bool>.Read(frame), Is.True, "same roster");
            Assert.That(ReadPackedUInt(frame), Is.Zero);
            Assert.That(Packer<bool>.Read(frame), Is.False);
            Assert.That(Packer<bool>.Read(frame), Is.False);
            Assert.That(Packer<bool>.Read(frame), Is.True, "tick 13 was uploaded again");
            frame.SkipBits((8 - frame.positionInBits % 8) % 8);
            Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));

            receiver.Read(frame, 13, 10);
            AssertInput(received, 11, 11);
            AssertInput(received, 12, 0);
            AssertInput(received, 13, 13);

            // Another receiver gets every record in full; nothing of it is restorable.
            using var foreign = sender.Write(13, 10, player: other);
            int foreignBits = foreign.positionInBits;
            foreign.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(foreign), Is.EqualTo(3));
            Assert.That(ReadPackedUInt(foreign), Is.EqualTo(1));
            ReadTickHeader(foreign, false, 1, false, out _);
            AssertRecord(foreign, 682, 11);
            Assert.That(ReadPackedUInt(foreign), Is.EqualTo(1));
            var deltas = ReadTickHeader(foreign, true, 1, true, out var patched);
            Assert.That(deltas[0], Is.False);
            AssertRecord(foreign, 682, 0, true, patched[0], 11);
            Assert.That(ReadPackedUInt(foreign), Is.EqualTo(1));
            deltas = ReadTickHeader(foreign, true, 1, true, out patched);
            Assert.That(deltas[0], Is.False);
            AssertRecord(foreign, 682, 13, true, patched[0], 0);
            Assert.That(foreign.positionInBits, Is.EqualTo(foreignBits));
        }

        private static void SetOwner(PredictedIdentity identity, PlayerID owner)
            => SetField(typeof(PredictedIdentity), identity, "_owner", owner);

        // The server's real upload path: the queued packet bits are consumed by PrepareInput.
        private static void UploadAndPrepare(PredictedIdentity identity, PlayerID sender, ulong tick, int? value)
        {
            if (value.HasValue)
            {
                using var packet = BitPackerPool.Get();
                Packer<bool>.Write(packet, true);
                Packer<TrackedInput>.Write(packet, new TrackedInput(value.Value));
                packet.ResetPositionAndMode(true);
                identity.QueueInput(packet, sender);
            }
            identity.PrepareInput(true, false, tick, false);
        }

        [Test]
        public void RepeatsRequireTheReceiverToHaveReceivedThePreviousTick()
        {
            using var sender = new InputTranscriptFixture("sender");
            sender.EnableVisibility();
            var sent = sender.AddInput(false, 670);
            for (ulong tick = 11; tick <= 15; tick++)
            {
                sent.Write(tick, new TrackedInput(5));
                sender.Capture(tick);
            }

            var timeline = new PlayerVisibilityTimeline();
            timeline.SetVisible(12, new PredictedObjectID(670), false);
            timeline.SetVisible(14, new PredictedObjectID(670), true);
            using var frame = sender.Write(15, 10, timeline);
            int end = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Assert.That(ReadPackedUInt(frame), Is.EqualTo(5));
            for (ulong tick = 11; tick <= 15; tick++)
            {
                bool visible = tick == 11 || tick >= 14;
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(visible ? 1 : 0), $"tick {tick}");
                if (!visible)
                    continue;
                // Tick 14 re-enters after two empty ticks, so its roster differs and it is written in full.
                bool repeat = ReadTickHeader(frame, tick > 11, 1, tick == 15, out _)[0];
                Assert.That(repeat, Is.EqualTo(tick == 15),
                    $"tick {tick}: an entry re-entering this receiver's view is written in full even when its value is unchanged");
                if (repeat)
                    continue;
                Assert.That(Packer<PredictedComponentID>.Read(frame),
                    Is.EqualTo(new PredictedComponentID(new PredictedObjectID(670), 0)));
                Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                frame.SkipBits((int)ReadPackedUInt(frame));
            }
            Assert.That(frame.positionInBits, Is.EqualTo(end));
        }

        [TestCase(50UL, 10UL)]
        [TestCase(20UL, 21UL)]
        public void SenderRejectsExpiredOrFutureBaselineInsteadOfTruncating(ulong tick, ulong baseline)
        {
            using var sender = new InputTranscriptFixture("sender");
            sender.AddInput(false, 650);
            Assert.Throws<MissingPredictionBaselineException>(() =>
            {
                using var frame = sender.Write(tick, baseline);
            });
        }

        [TestCase(50UL, 10UL, 32U)]
        [TestCase(20UL, 17UL, 0U)]
        [TestCase(20UL, 17UL, 2U)]
        [TestCase(20UL, 17UL, 4U)]
        [TestCase(20UL, 17UL, uint.MaxValue)]
        [TestCase(10UL, 11UL, 0U)]
        public void ReceiverRejectsAnIncompleteOrInvalidIntervalBeforeApplyingInputs(
            ulong tick, ulong baseline, uint count)
        {
            using var receiver = new InputTranscriptFixture("receiver");
            var received = receiver.AddInput(false, 650);
            using var frame = BitPackerPool.Get();
            Packer<PackedUInt>.Write(frame, count);
            // No entry data is needed: the interval itself is not a verified transcript.
            Assert.Throws<MissingPredictionBaselineException>(() => receiver.Read(frame, tick, baseline));
            Assert.That(received.Count, Is.Zero);
        }

        private static bool[] ReadTickHeader(BitPacker frame, bool afterFirstTick, int count, bool expectSameRoster, out bool[] deltas)
        {
            var repeats = new bool[count];
            deltas = new bool[count];
            if (count == 0)
                return repeats;
            bool sameRoster = false;
            if (afterFirstTick)
            {
                sameRoster = Packer<bool>.Read(frame);
                Assert.That(sameRoster, Is.EqualTo(expectSameRoster), "same-roster flag");
            }
            Assert.That(ReadPackedUInt(frame), Is.Zero, "view offset count");
            if (sameRoster)
                for (int i = 0; i < count; i++)
                {
                    repeats[i] = Packer<bool>.Read(frame);
                    deltas[i] = !repeats[i] && Packer<bool>.Read(frame);
                    if (!repeats[i] && !deltas[i])
                        Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
                }
            frame.SkipBits((8 - frame.positionInBits % 8) % 8);
            return repeats;
        }

        private static void PadToByte(BitPacker frame)
            => frame.WriteBits(0UL, (byte)((8 - frame.positionInBits % 8) % 8));

        private static void AssertRecord(BitPacker frame, uint objectId, int value,
            bool sameRoster = false, bool delta = false, int previousValue = 0)
        {
            if (!sameRoster)
            {
                Assert.That(Packer<PredictedComponentID>.Read(frame),
                    Is.EqualTo(new PredictedComponentID(new PredictedObjectID(objectId), 0)));
                Assert.That(Packer<bool>.Read(frame), Is.False, "no receiver-owned input to restore");
            }
            if (delta)
            {
                using var baseline = BitPackerPool.Get();
                Packer<bool>.Write(baseline, true);
                Packer<TrackedInput>.Write(baseline, new TrackedInput(previousValue));
                using var decoded = BitPackerPool.Get();
                InputHistoryDelta.Read(frame, frame.buffer.Length * 8, new BitData(baseline), decoded);
                decoded.ResetPositionAndMode(true);
                Assert.That(Packer<bool>.Read(decoded), Is.True);
                Assert.That(Packer<TrackedInput>.Read(decoded).id, Is.EqualTo(value));
                return;
            }
            var declaredBits = ReadPackedUInt(frame);
            var origin = frame.positionInBits;
            Assert.That(Packer<bool>.Read(frame), Is.True);
            Assert.That(Packer<TrackedInput>.Read(frame).id, Is.EqualTo(value));
            Assert.That(frame.positionInBits - origin, Is.EqualTo((int)declaredBits));
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

        private static void RegisterSystem(
            PredictionManager manager,
            PredictedIdentity identity)
        {
            var systems = GetField<List<PredictedIdentity>>(
                typeof(PredictionManager),
                manager,
                "_systems");
            systems.Add(identity);
            SetField(
                typeof(PredictionManager),
                manager,
                "_systemsCount",
                systems.Count);
            var instanceMap =
                GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager),
                    manager,
                    "_instanceMap");
            instanceMap[identity.id] = identity;
            if (identity.hasInput)
            {
                var inputCount = GetField<int>(
                    typeof(PredictionManager),
                    manager,
                    "_inputHistorySystems");
                SetField(
                    typeof(PredictionManager),
                    manager,
                    "_inputHistorySystems",
                    inputCount + 1);
            }
        }

        private static void CaptureInputHistory(PredictionManager manager, ulong tick)
            => typeof(PredictionManager).GetMethod("CaptureInputHistory", InstanceFields)
                .Invoke(manager, new object[] { tick });

        private static void WriteVisibilityInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong baselineTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteVisibilityInputHistory",
                InstanceFields);
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

        private static void ReadInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong serverTick,
            ulong baselineTick,
            int frameEndBit)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ReadInputHistory",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(manager, new object[] { frame, serverTick, baselineTick, frameEndBit });
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static uint ReadPackedUInt(BitPacker frame)
        {
            PackedUInt value = default;
            Packer<PackedUInt>.Read(frame, ref value);
            return value.value;
        }

        private static void AssertInput(
            History<TrackedInput> history,
            ulong tick,
            int expected)
        {
            Assert.That(history.TryGet(tick, out var value), Is.True,
                $"missing decoded input at tick {tick}");
            Assert.That(value.id, Is.EqualTo(expected),
                $"wrong decoded input value at tick {tick}");
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

    internal sealed class InputTranscriptFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _objects = new();
        private readonly List<PredictedIdentity> _identities = new();
        internal readonly PredictionManager manager;

        internal InputTranscriptFixture(string name, int tickRate = 20)
        {
            var gameObject = new GameObject($"Input transcript {name}");
            _objects.Add(gameObject);
            manager = gameObject.AddComponent<PredictionManager>();
            Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", tickRate);
        }

        internal History<TrackedInput> AddInput(bool deterministic, uint objectId)
        {
            var gameObject = new GameObject($"Input transcript identity {objectId}");
            _objects.Add(gameObject);
            PredictedIdentity identity = deterministic
                ? gameObject.AddComponent<DeterministicInputProbe>()
                : gameObject.AddComponent<StatefulInputProbe>();
            _identities.Add(identity);
            identity.id = new PredictedComponentID(new PredictedObjectID(objectId), 0);
            Set(typeof(PredictedIdentity), identity, "<predictionManager>k__BackingField", manager);
            var history = new History<TrackedInput>(256);
            Set(deterministic ? typeof(DeterministicIdentity<TrackedInput, EmptyState>)
                : typeof(PredictedIdentity<TrackedInput, EmptyState>), identity, "_inputHistory", history);
            var systems = (List<PredictedIdentity>)typeof(PredictionManager)
                .GetField("_systems", Fields).GetValue(manager);
            systems.Add(identity);
            Set(typeof(PredictionManager), manager, "_systemsCount", systems.Count);
            Set(typeof(PredictionManager), manager, "_inputHistorySystems", _identities.Count);
            var map = (Dictionary<PredictedComponentID, PredictedIdentity>)typeof(PredictionManager)
                .GetField("_instanceMap", Fields).GetValue(manager);
            map.Add(identity.id, identity);
            return history;
        }

        internal void EnableVisibility()
        {
            var gameObject = new GameObject("Input transcript hierarchy");
            _objects.Add(gameObject);
            var hierarchy = gameObject.AddComponent<PredictedHierarchy>();
            Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);
        }

        internal void Capture(ulong tick)
        {
            Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
            Invoke("CaptureInputHistory", tick);
        }

        internal PredictedIdentity lastIdentity => _identities[_identities.Count - 1];

        // What this peer's upload path would have recorded for the tick.
        internal void RecordUpload(ulong tick, uint objectId, int value)
        {
            using var block = BitPackerPool.Get();
            block.ResetPositionAndMode(false);
            Packer<bool>.Write(block, true);
            Packer<TrackedInput>.Write(block, new TrackedInput(value));
            var spans = new List<PredictionManager.InputHistorySpan>
            {
                new() { id = new PredictedComponentID(new PredictedObjectID(objectId), 0), bitOrigin = 0, bitLength = block.positionInBits }
            };
            manager.RecordUploadedInputs(tick, block, spans);
        }

        internal void SetVerifiedTick(ulong tick) => Set(typeof(PredictionManager), manager, "_verifiedServerTick", tick);

        internal List<ulong> UploadedTicks()
        {
            var ledger = (System.Collections.IDictionary)typeof(PredictionManager)
                .GetField("_uploadedInputs", Fields).GetValue(manager);
            var ticks = new List<ulong>();
            foreach (var key in ledger.Keys)
                ticks.Add((ulong)key);
            return ticks;
        }

        internal void Despawn(PredictedIdentity identity)
        {
            var map = (Dictionary<PredictedComponentID, PredictedIdentity>)typeof(PredictionManager)
                .GetField("_instanceMap", Fields).GetValue(manager);
            map.Remove(identity.id);
            var systems = (List<PredictedIdentity>)typeof(PredictionManager).GetField("_systems", Fields).GetValue(manager);
            systems.Remove(identity);
            Set(typeof(PredictionManager), manager, "_systemsCount", systems.Count);
            _identities.Remove(identity);
            Object.DestroyImmediate(identity.gameObject);
        }

        internal BitPacker Write(ulong tick, ulong baseline, PlayerVisibilityTimeline timeline = null,
            PlayerID player = default)
        {
            Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
            var frame = BitPackerPool.Get();
            try
            {
                Invoke("WriteVisibilityInputHistory", player, frame, baseline,
                    timeline ?? new PlayerVisibilityTimeline());
                return frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        internal void Read(BitPacker frame, ulong tick, ulong baseline)
        {
            Parse(frame, tick, baseline);
            for (ulong inputTick = baseline + 1; inputTick <= tick; inputTick++)
                Apply(inputTick);
        }

        internal void Parse(BitPacker frame, ulong tick, ulong baseline)
        {
            var end = frame.positionInBits;
            frame.ResetPositionAndMode(true);
            Invoke("ReadInputHistory", frame, tick, baseline, end);
            Assert.That(frame.positionInBits, Is.EqualTo(end), "input section consumed an incorrect bit count");
        }

        internal void Apply(ulong tick) => Invoke("ApplyVerifiedInputs", tick);

        private void Invoke(string name, params object[] args)
        {
            try
            {
                typeof(PredictionManager).GetMethod(name, Fields).Invoke(manager, args);
            }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void Set(Type type, object instance, string name, object value)
            => type.GetField(name, Fields).SetValue(instance, value);

        internal static void DisposeManagerCaches(PredictionManager target)
        {
            if (!target)
                return;
            typeof(PredictionManager).GetMethod("ClearVerifiedInputTranscript", Fields).Invoke(target, null);
            typeof(PredictionManager).GetMethod("DisposeInputBlockCache", Fields).Invoke(target, null);
            typeof(PredictionManager).GetMethod("DisposeLifecycleHistory", Fields).Invoke(target, null);
        }

        public void Dispose()
        {
            DisposeManagerCaches(manager);
            foreach (var identity in _identities)
                identity.ReleasePredictionStateForPool();
            for (var i = _objects.Count - 1; i >= 0; i--)
                Object.DestroyImmediate(_objects[i]);
        }
    }

    public sealed class PlayerRosterTranscriptConsumer : DeterministicIdentity<PredictedPlayersState>
    {
        public void AddPlayer(PlayerID player) => currentState.players.Add(player);
        public void RemovePlayer(PlayerID player) => currentState.players.Remove(player);
    }
}
