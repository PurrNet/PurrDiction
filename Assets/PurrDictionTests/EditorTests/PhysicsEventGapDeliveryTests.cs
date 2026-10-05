using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
#if UNITY_PHYSICS_3D
    public sealed class PhysicsEventGapDeliveryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong BaselineTick = 10;
        private const ulong CollisionTick = 11;
        private const ulong FinalTick = 14;

        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [Test]
        public void DeliveredCollisionFrameRaisesOriginalTickOnceThroughRealReconciliation()
        {
            using var fixture = new EventFixture();
            using var collision = fixture.WriteEventSection(CollisionTick);

            Assert.That(collision.RecordCount, Is.EqualTo(1),
                "the positive control must carry an actual physics state record");
            fixture.QueueFrame(CollisionTick, collision);
            for (ulong tick = CollisionTick + 1; tick <= FinalTick; tick++)
            {
                using var empty = fixture.WriteEventSection(tick);
                fixture.QueueFrame(tick, empty);
            }
            fixture.Reconcile();

            Assert.That(fixture.VerifiedTick, Is.EqualTo(FinalTick));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new[] { CollisionTick }),
                "the delivered event must fire once at its authoritative tick while all " +
                "four contiguous verified frames are applied in the same batch");
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 4f }));
            Assert.That(fixture.callbackWasVerifiedReplay, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void SkippedCollisionFrameMustStillRaiseOriginalAuthoritativeTick()
        {
            using var fixture = new EventFixture();
            using var lostCollision = fixture.WriteEventSection(CollisionTick);
            using var laterWithCollisionHistory = fixture.WriteEventSection(FinalTick);

            // Compare the actual addressed event wire payload, changing ONLY the earlier
            // authoritative event. Baseline 10 and current state 14 remain identical.
            fixture.ReplaceHistoricalCollision(false);
            using var laterWithoutCollisionHistory = fixture.WriteEventSection(FinalTick);
            bool identicalWire = laterWithCollisionHistory.HasSameBits(laterWithoutCollisionHistory);
            fixture.ReplaceHistoricalCollision(true);

            Assert.That(lostCollision.RecordCount, Is.EqualTo(1));
            Assert.That(fixture.AuthoritativeCollisionCount, Is.EqualTo(1),
                "the event must still exist in retained server history, excluding expiry " +
                "or a missing PhysX contact as explanations");
            Assert.That(identicalWire, Is.False,
                "the later wire payload must preserve evidence of the unacknowledged event");
            Assert.That(laterWithCollisionHistory.HistoricalTicks, Is.EqualTo(new[] { CollisionTick }));

            // Frames 11-13 never reach the receiver. The real manager accepts 14, replays
            // 11-13 as verified gap ticks, then reads and dispatches the addressed events.
            // Physics is disabled: the seeded authoritative record is the sole event source.
            fixture.QueueFrame(FinalTick, laterWithCollisionHistory);
            fixture.Reconcile();

            Assert.That(fixture.VerifiedTick, Is.EqualTo(FinalTick));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new[] { CollisionTick }),
                "a skipped frame must not lose its authoritative collision callback");
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 4f }));
            Assert.That(fixture.callbackWasVerifiedReplay, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void GapPreservesEventsOnEveryOriginalTickAndTheirWithinTickOrder()
        {
            using var fixture = new EventFixture();
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(21), SeedEvent.Collision(22));
            fixture.SetEvents(14, SeedEvent.Collision(41));
            using var frame = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 12, 12, 14 }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 11f, 21f, 22f, 41f }),
                "multiple events for the same identity on one tick must remain ordered and distinct");
            Assert.That(fixture.callbackWasVerifiedReplay, Is.All.True);
        }

        [Test]
        public void GapPreservesTriggerAndCollisionEnterStayExitOrder()
        {
            using var fixture = new EventFixture();
            fixture.SetEvents(11, SeedEvent.Trigger(PhysicsEventType.Enter));
            fixture.SetEvents(12, SeedEvent.Trigger(PhysicsEventType.Stay), SeedEvent.Collision(2));
            fixture.SetEvents(13, SeedEvent.Trigger(PhysicsEventType.Exit),
                SeedEvent.Collision(3, PhysicsEventType.Stay), SeedEvent.Collision(4, PhysicsEventType.Exit));
            using var frame = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 12, 12, 13, 13, 13 }));
            Assert.That(fixture.callbackKinds, Is.EqualTo(new[]
            {
                "trigger/Enter", "trigger/Stay", "collision/Enter",
                "trigger/Exit", "collision/Stay", "collision/Exit"
            }));
            Assert.That(fixture.callbackWasVerifiedReplay, Is.All.True);
        }

        [Test]
        public void OverlappingTranscriptsDuplicateAndLateFramesDoNotRedispatchEvents()
        {
            using var fixture = new EventFixture();
            fixture.SetEvents(13, SeedEvent.Collision(13));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var frame12 = fixture.WriteEventSection(12);
            using var frame13 = fixture.WriteEventSection(13);
            using var frame14 = fixture.WriteEventSection(14);
            fixture.QueueFrame(12, frame12);
            fixture.Reconcile();
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11 }));

            // The sender still uses ACK baseline 10 for all three received packets.
            fixture.QueueFrame(14, frame14);
            fixture.QueueFrame(14, frame14);
            fixture.QueueFrame(13, frame13);
            fixture.Reconcile();

            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 13, 14 }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 4f, 13f, 14f }));
        }

        [Test]
        public void SilentGapAfterADeliveredEventDoesNotReuseItsPreviousTickList()
        {
            using var fixture = new EventFixture();
            using var frame11 = fixture.WriteEventSection(11);
            using var frame14 = fixture.WriteEventSection(14);
            fixture.QueueFrame(11, frame11);
            fixture.Reconcile();
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11 }));
            fixture.QueueFrame(14, frame14);
            fixture.Reconcile();

            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11 }),
                "sparse ReadOrPrevious state must not turn the old event list into a new event at tick 12");
            Assert.That(fixture.RecordFailure, Is.False);
        }

#if UNITY_PHYSICS_2D
        [Test]
        public void TwoDimensionalGapPreservesOriginalTicksAndContactPayloads()
        {
            using var fixture = new EventFixture(use2D: true);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(21), SeedEvent.Collision(22));
            fixture.SetEvents(14, SeedEvent.Collision(41));
            using var frame = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 12, 12, 14 }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 11f, 21f, 22f, 41f }),
                "2D markers are serialized contact-point x coordinates, not synthetic callbacks");
            Assert.That(fixture.callbackWasVerifiedReplay, Is.All.True);
        }

        [Test]
        public void TwoDimensionalOverlappingHistoryKeepsTriggerAndCollisionOrderWithoutDuplicates()
        {
            using var fixture = new EventFixture(use2D: true);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Trigger(PhysicsEventType.Enter), SeedEvent.Collision(12));
            fixture.SetEvents(13, SeedEvent.Trigger(PhysicsEventType.Stay), SeedEvent.Collision(13, PhysicsEventType.Stay));
            fixture.SetEvents(14, SeedEvent.Trigger(PhysicsEventType.Exit), SeedEvent.Collision(14, PhysicsEventType.Exit));
            using var frame12 = fixture.WriteEventSection(12);
            using var frame13 = fixture.WriteEventSection(13);
            using var frame14 = fixture.WriteEventSection(14);
            fixture.QueueFrame(12, frame12);
            fixture.Reconcile();
            fixture.QueueFrame(14, frame14);
            fixture.QueueFrame(14, frame14);
            fixture.QueueFrame(13, frame13);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 12, 12, 13, 13, 14, 14 }));
            Assert.That(fixture.callbackKinds, Is.EqualTo(new[]
            {
                "collision/Enter", "trigger/Enter", "collision/Enter", "trigger/Stay",
                "collision/Stay", "trigger/Exit", "collision/Exit"
            }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 11f, 0f, 12f, 0f, 13f, 0f, 14f }));
        }
#endif

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void HistoricalVisibilityDoesNotLeakHiddenEventsAfterReentry(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetVisible(10, false);
            fixture.SetVisible(12, true);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var frame = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 12, 14 }),
                "visibility at the original tick must suppress 11 even though the endpoint is visible again at 14");
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 12f, 14f }));
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void VisibleHistoricalEventSurvivesLaterHideWhenItsEndpointStillResolves(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetVisible(12, false);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var frame = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11 }),
                "a current hidden policy must not rewrite the historical visibility of a still-resolvable endpoint");
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void FirstContactFullFrameDeliversOnlyItsCurrentTickEvents(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetReceiverVerifiedTick(0);
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var frame = fixture.WriteEventSection(14, fullFrame: true);
            Assert.That(frame.HistoricalTicks, Is.Empty,
                "a full snapshot is a state-reset boundary, not a historical callback replay");
            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 14 }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 14f }));
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void FullResyncDoesNotReplayOldCallbacksIntoTheReplacementState(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(13, SeedEvent.Collision(13));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var first = fixture.WriteEventSection(11);
            fixture.QueueFrame(11, first);
            fixture.Reconcile();
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11 }));

            using var pending = fixture.WriteEventSection(12);
            using var reset = fixture.WriteEventSection(14, fullFrame: true);
            fixture.QueueFrame(12, pending);
            fixture.QueueFrame(14, reset); // actual receive handler supersedes queued older deltas
            fixture.Reconcile();

            Assert.That(reset.HistoricalTicks, Is.Empty);
            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 14 }),
                "earlier effects are already represented by the full state; only its current event may run");
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void RealAuthoritativeCaptureRetainsSilentTicksForTheNextDelta(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.ClearSenderEventHistory();
            for (ulong tick = BaselineTick; tick <= FinalTick; tick++)
            {
                fixture.CaptureEvents(tick,
                    tick == CollisionTick ? new[] { SeedEvent.Collision(11) } : Array.Empty<SeedEvent>());
            }
            for (ulong tick = BaselineTick; tick <= FinalTick; tick++)
                Assert.That(fixture.HasExactEventTick(tick), Is.True,
                    "equal empty event lists must retain exact tick coverage, not sparse previous-state semantics");

            using var frame = fixture.WriteEventSection(FinalTick);
            Assert.That(frame.HistoricalTicks, Is.EqualTo(new[] { CollisionTick }));
            fixture.QueueFrame(FinalTick, frame);
            fixture.Reconcile();
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new[] { CollisionTick }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 11f }));
        }

        [TestCase("batch-count")]
        [TestCase("zero-tick")]
        [TestCase("duplicate-tick")]
        [TestCase("descending-tick")]
        [TestCase("current-tick")]
        [TestCase("record-count")]
        [TestCase("record-length")]
        public void MalformedHistoryWaitsForCheckpointWithoutPartialCallbacksThenOrdinaryDeliveryResumes(string corruption)
        {
            using var fixture = new EventFixture();
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var malformed = fixture.WriteMalformedEventSection(corruption);
            ulong originalAck = fixture.AcknowledgedTick;
            fixture.SetReceiverPendingHistoryResync(originalAck); // suppress transport RPCs on this synthetic manager
            fixture.QueueFrame(14, malformed);

            LogAssert.Expect(LogType.Error, new Regex("Cannot apply prediction frame 14: System.InvalidOperationException:"));
            Assert.DoesNotThrow(() => fixture.Reconcile());
            Assert.That(fixture.callbackTicks, Is.Empty,
                "except for the invalid count, a valid earlier batch precedes the malformed batch; " +
                "the whole transcript must be checked before any callback mutates gameplay");
            Assert.That(fixture.VerifiedTick, Is.EqualTo(BaselineTick));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(originalAck));
            Assert.That(fixture.IsReplayingOrSimulating, Is.False,
                "rejected wire data must unwind the manager's replay phase");
            Assert.That(fixture.RecordFailure, Is.True);
            Assert.That(fixture.RequiresCheckpoint, Is.True);
            Assert.That(fixture.HistoryResyncPending, Is.True);
            Assert.That(fixture.RequiredResyncTick, Is.EqualTo(14));

            using var valid = fixture.WriteEventSection(14);
            fixture.QueueFrame(14, valid);
            fixture.Reconcile();
            Assert.That(fixture.VerifiedTick, Is.EqualTo(BaselineTick));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(originalAck));
            Assert.That(fixture.callbackTicks, Is.Empty,
                "event validation follows hierarchy decoding; the frame has no general transaction rollback, " +
                "so a later delta cannot clear a frame-application failure");

            using var checkpoint = fixture.WriteEventSection(14, fullFrame: true);
            fixture.QueueFrame(14, checkpoint);
            fixture.Reconcile();
            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(14));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.RequiresCheckpoint, Is.False);
            Assert.That(fixture.HistoryResyncPending, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 14 }),
                "the accepted checkpoint replaces the rejected interval and delivers only its current events");
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 14f }));

            fixture.SetEvents(15, SeedEvent.Collision(15));
            using var resumed = fixture.WriteEventSection(15, baselineTick: 14);
            fixture.QueueFrame(15, resumed);
            fixture.Reconcile();
            Assert.That(fixture.VerifiedTick, Is.EqualTo(15));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(15));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 14, 15 }));
            Assert.That(fixture.callbackSpeeds, Is.EqualTo(new[] { 14f, 15f }));
        }

        [TestCase(false, 20, 32)]
        [TestCase(false, 60, 96)]
#if UNITY_PHYSICS_2D
        [TestCase(true, 20, 32)]
        [TestCase(true, 60, 96)]
#endif
        public void MaximumWindowKeepsTheAcknowledgedBaselineUsableAndOlderBaselinesForceFull(
            bool use2D, int tickRate, int expectedWindow)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetTickRate(tickRate);
            Assert.That(PredictionManager.VerifiedHistoryWindowTicks(tickRate), Is.EqualTo(expectedWindow));
            ulong lastDeltaTick = BaselineTick + (ulong)expectedWindow;
            for (ulong tick = BaselineTick; tick <= lastDeltaTick + 1; tick++)
                fixture.SetEvents(tick, tick == CollisionTick || tick >= lastDeltaTick - 1
                    ? new[] { SeedEvent.Collision((float)tick) } : Array.Empty<SeedEvent>());

            Assert.That(fixture.PrepareOutboundFullFrame(lastDeltaTick, BaselineTick), Is.False,
                "the retained ACK window remains usable even though it can exceed one client's replay budget");
            using var maximum = fixture.WriteEventSection(lastDeltaTick);
            Assert.That(maximum.HistoricalTicks, Is.EqualTo(new ulong[] { 11, lastDeltaTick - 1 }));
            // The sender's ACK remains 10 while the client verifies later frames. Retaining
            // that older baseline must not expand the amount of physics replay per batch.
            for (ulong tick = BaselineTick + 32; tick <= lastDeltaTick; tick += 32)
            {
                fixture.SetReceiverPredictionHead(tick + 4);
                using var nextChunk = fixture.WriteEventSection(tick, BaselineTick);
                fixture.QueueFrame(tick, nextChunk);
                fixture.Reconcile();
                Assert.That(fixture.VerifiedTick, Is.EqualTo(tick));
            }
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, lastDeltaTick - 1, lastDeltaTick }));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(lastDeltaTick));

            Assert.That(fixture.PrepareOutboundFullFrame(lastDeltaTick + 1, BaselineTick), Is.True,
                "the actual server frame selector must reset instead of clipping an event outside its retained window");
            var tooOld = Assert.Throws<TargetInvocationException>(() =>
            {
                using var ignored = fixture.WriteEventSection(lastDeltaTick + 1);
            });
            Assert.That(tooOld.InnerException, Is.TypeOf<InvalidOperationException>());

            Assert.That(fixture.PrepareOutboundFullFrame(lastDeltaTick + 1, fixture.AcknowledgedTick), Is.False,
                "advancing the ACK to a successfully applied frame restores a usable delta baseline");
            using var next = fixture.WriteEventSection(lastDeltaTick + 1, fixture.AcknowledgedTick);
            fixture.QueueFrame(lastDeltaTick + 1, next);
            fixture.Reconcile();
            Assert.That(fixture.VerifiedTick, Is.EqualTo(lastDeltaTick + 1));
            Assert.That(fixture.callbackTicks,
                Is.EqualTo(new ulong[] { 11, lastDeltaTick - 1, lastDeltaTick, lastDeltaTick + 1 }));
            Assert.That(fixture.RecordFailure, Is.False);
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void RejectedFutureFrameCannotDrivePredictionHeadOrPacingAfterAnAcceptedFrame(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetTickRate(60);
            // The receiver's window (32 ticks at the minimum rate) cannot read tick 100's input transcript.
            fixture.SetReceiverTickRate(20);
            for (ulong tick = BaselineTick; tick <= 100; tick++)
                fixture.SetEvents(tick, tick == 11 || tick == 20 || tick >= 99
                    ? new[] { SeedEvent.Collision((float)tick) } : Array.Empty<SeedEvent>());
            const ulong originalHead = 24;
            fixture.SetReceiverPredictionHead(originalHead);
            fixture.SetReceiverPendingHistoryResync(BaselineTick);
            using var accepted = fixture.WriteEventSection(20);
            using var rejected = fixture.WriteEventSection(100);
            fixture.QueueFrame(20, accepted);
            fixture.QueueFrame(100, rejected, inputAck: 0, inputMargin: -100, inputSlackMs: -1000);
            LogAssert.Expect(LogType.Error, new Regex("Cannot apply prediction frame 100: Invalid authoritative input history window"));
            fixture.Reconcile();

            Assert.That(fixture.VerifiedTick, Is.EqualTo(20));
            Assert.That(fixture.AcknowledgedTick, Is.EqualTo(20));
            Assert.That(fixture.LatestAcceptedFrameTick, Is.EqualTo(20));
            Assert.That(fixture.PredictionHead, Is.LessThanOrEqualTo(originalHead),
                "rejected future feedback must not bypass the replay ceiling through an end-of-batch starvation jump");
            Assert.That(fixture.InputMarginFeedbackTick, Is.Zero);
            Assert.That(fixture.InputSlackFeedbackTick, Is.Zero,
                "neither pacing channel may consume feedback carried only by a rejected frame");
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 11, 20 }));
            Assert.That(fixture.RecordFailure, Is.True);
            Assert.That(fixture.RequiresCheckpoint, Is.True,
                "a delta the receiver cannot read breaks the stream until the next full frame");
            Assert.That(fixture.IsReplayingOrSimulating, Is.False);
        }

        [Test]
        public void InputCacheRetainsTheConfiguredAckWindowAndResizesAfterTickRateChanges()
        {
            using var fixture = new EventFixture();
            fixture.SetTickRate(20);
            fixture.CacheInputTick(10);
            fixture.CacheInputTick(42);
            Assert.That(fixture.InputCacheCapacity, Is.EqualTo(33));
            Assert.That(fixture.HasCachedInputTick(10), Is.True);
            fixture.CacheInputTick(43);
            Assert.That(fixture.HasCachedInputTick(10), Is.False);

            fixture.SetTickRate(60);
            fixture.CacheInputTick(138);
            Assert.That(fixture.InputCacheCapacity, Is.EqualTo(97));
            Assert.That(fixture.HasCachedInputTick(10), Is.False,
                "rate growth cannot reconstruct an already expired historical tick");
            Assert.That(fixture.HasCachedInputTick(42), Is.True,
                "rate growth preserves every still-retained tick through the new window boundary");
            Assert.That(fixture.HasCachedInputTick(43), Is.True);
            fixture.CacheInputTick(139);
            Assert.That(fixture.HasCachedInputTick(42), Is.False);

            fixture.SetTickRate(20);
            fixture.CacheInputTick(200);
            Assert.That(fixture.InputCacheCapacity, Is.EqualTo(33));
            Assert.That(fixture.HasCachedInputTick(138), Is.False,
                "resizing must not leave the previous session-rate cache addressable");
            Assert.That(fixture.HasCachedInputTick(200), Is.True);
        }

        [TestCase(false)]
#if UNITY_PHYSICS_2D
        [TestCase(true)]
#endif
        public void MissingExactEventTickForcesFullEvenWhenAPreviousStateExists(bool use2D)
        {
            using var fixture = new EventFixture(use2D);
            fixture.ClearSenderEventHistory();
            fixture.SetEvents(10);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(13);
            fixture.SetEvents(14, SeedEvent.Collision(14));
            Assert.That(fixture.HasExactEventTick(12), Is.False);
            Assert.That(fixture.HasPreviousEventState(12), Is.True,
                "the hole must be real exact-tick loss, not an entirely empty history");
            Assert.That(fixture.PrepareOutboundFullFrame(14, BaselineTick), Is.True);
            var incomplete = Assert.Throws<TargetInvocationException>(() =>
            {
                using var ignored = fixture.WriteEventSection(14);
            });
            Assert.That(incomplete.InnerException, Is.TypeOf<InvalidOperationException>());

            using var reset = fixture.WriteEventSection(14, fullFrame: true);
            Assert.That(reset.HistoricalTicks, Is.Empty);
            fixture.QueueFrame(14, reset);
            fixture.Reconcile();
            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.callbackTicks, Is.EqualTo(new ulong[] { 14 }),
                "a forced full snapshot applies current callbacks only; it must not invent the missing tick");
            Assert.That(fixture.RecordFailure, Is.False);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
#if UNITY_PHYSICS_2D
        [TestCase(true, false)]
        [TestCase(true, true)]
#endif
        public void HistoricalCallbacksResolveLogicalEndpointsAtTheirReplayTick(bool use2D, bool absentBeforeReplay)
        {
            using var fixture = new EventFixture(use2D);
            fixture.SetEvents(11, SeedEvent.Collision(11));
            fixture.SetEvents(12, SeedEvent.Collision(12));
            fixture.SetEvents(14, SeedEvent.Collision(14));
            using var frame = fixture.WriteEventSection(14);
            if (absentBeforeReplay)
                fixture.UnregisterCallbackEndpoint();
            else
                fixture.afterCallback = fixture.UnregisterCallbackEndpoint;

            fixture.QueueFrame(14, frame);
            fixture.Reconcile();

            Assert.That(fixture.CallbackEndpointIsRegistered, Is.False);
            Assert.That(fixture.VerifiedTick, Is.EqualTo(14));
            Assert.That(fixture.RecordFailure, Is.False);
            Assert.That(fixture.callbackTicks, Is.EqualTo(absentBeforeReplay
                ? Array.Empty<ulong>() : new ulong[] { 11 }),
                "the first callback may remove its own logical endpoint; subsequent records must " +
                "resolve the current identity map rather than keeping a captured component reference");
            Assert.That(fixture.callbackWasVerifiedReplay, Is.All.True);
        }

        private readonly struct SeedEvent
        {
            public readonly bool trigger;
            public readonly PhysicsEventType type;
            public readonly float marker;

            private SeedEvent(bool trigger, PhysicsEventType type, float marker)
            {
                this.trigger = trigger;
                this.type = type;
                this.marker = marker;
            }

            public static SeedEvent Collision(float marker, PhysicsEventType type = PhysicsEventType.Enter) =>
                new(false, type, marker);

            public static SeedEvent Trigger(PhysicsEventType type) => new(true, type, 0);
        }

        private readonly struct OutboundSelection
        {
            public readonly bool fullFrame;
            public readonly ulong baselineTick;
            public readonly ulong preparedTick;

            public OutboundSelection(PlayerPacker frame, ulong baselineTick)
            {
                fullFrame = frame.fullFrame;
                this.baselineTick = frame.preparedBaselineTick;
                preparedTick = frame.preparedFrameTick;
            }
        }

        private sealed class EventSection : IDisposable
        {
            public readonly BitPacker packer;
            public readonly int bitCount;
            public readonly ulong baselineTick;
            public readonly bool fullFrame;

            public EventSection(BitPacker packer, ulong baselineTick, bool fullFrame)
            {
                this.packer = packer;
                bitCount = packer.positionInBits;
                this.baselineTick = baselineTick;
                this.fullFrame = fullFrame;
            }

            public List<ulong> HistoricalTicks
            {
                get
                {
                    packer.ResetPositionAndMode(true);
                    return ReadHistoricalTicks();
                }
            }

            private List<ulong> ReadHistoricalTicks()
            {
                var ticks = new List<ulong>();
                uint count = Packer<PackedUInt>.Read(packer).value;
                for (uint i = 0; i < count; i++)
                {
                    ticks.Add(baselineTick + Packer<PackedUInt>.Read(packer).value);
                    AddressedPredictionRecords.ReadSection(
                        source: packer, readRecord: (_, _, _, _) => { });
                }
                return ticks;
            }

            public int RecordCount
            {
                get
                {
                    int count = 0;
                    packer.ResetPositionAndMode(true);
                    ReadHistoricalTicks();
                    AddressedPredictionRecords.ReadSection(
                        source: packer, readRecord: (_, _, _, _) => count++);
                    return count;
                }
            }

            public bool HasSameBits(EventSection other)
            {
                if (bitCount != other.bitCount)
                    return false;
                packer.ResetPositionAndMode(true);
                other.packer.ResetPositionAndMode(true);
                for (int i = 0; i < bitCount; i++)
                {
                    if (packer.ReadBits(1) != other.packer.ReadBits(1))
                        return false;
                }
                return true;
            }

            public void Dispose() => packer.Dispose();
        }

        private sealed class EventFixture : IDisposable
        {
            private readonly GameObject _networkObject = new("Physics gap test network");
            private readonly GameObject _senderObject = new("Physics gap sender");
            private readonly GameObject _receiverObject = new("Physics gap receiver");
            private readonly GameObject _senderPhysicsObject = new("Physics gap sender events");
            private readonly GameObject _receiverPhysicsObject = new("Physics gap receiver events");
            private readonly GameObject _hierarchyObject = new("Physics gap sender hierarchy");
            private readonly GameObject _callbackObject = new("Physics gap collision receiver");
            private readonly NetworkManager _network;
            private readonly PredictionManager _sender;
            private readonly PredictionManager _receiver;
            private readonly PredictedIdentity _senderPhysics;
            private readonly PredictedHierarchy _hierarchy;
            private PredictedIdentity _callbackIdentity;
            private readonly History<FULL_STATE<PredictedPhysicsData>> _senderVerified;
#if UNITY_PHYSICS_2D
            private readonly bool _use2D;
            private readonly History<FULL_STATE<PredictedPhysics2DData>> _senderVerified2D;
#endif
            private readonly double _previousCadence;
            private ulong _lastOutboundPreparedTick = BaselineTick;
            private readonly PlayerVisibilityTimeline _visibility = new();
            private readonly PredictedComponentID _callbackId =
                new(new PredictedObjectID(710), 0);

            public readonly List<ulong> callbackTicks = new();
            public readonly List<float> callbackSpeeds = new();
            public readonly List<string> callbackKinds = new();
            public readonly List<bool> callbackWasVerifiedReplay = new();
            public Action afterCallback;

            public EventFixture(bool use2D = false)
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = _networkObject.AddComponent<NetworkManager>();
                _sender = CreateManager(_senderObject);
                _receiver = CreateManager(_receiverObject);
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkIdentity), _receiver, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), _receiver, "_isSpawnedClient", true);
                Set(typeof(PredictionManager), _receiver, "_verifiedServerTick", BaselineTick);
                Set(typeof(PredictionManager), _receiver, "_awaitingFullFrame", false);
                Set(typeof(PredictionManager), _receiver, "_latestFrameServerTick", BaselineTick);

                // Builtin system ID 1 is always visible. A real hierarchy object selects the
                // production aggregate physics projection path in the addressed writer.
                var physicsId = new PredictedComponentID(new PredictedObjectID(1), 2);
#if UNITY_PHYSICS_2D
                _use2D = use2D;
                if (use2D)
                {
                    var senderPhysics = _senderPhysicsObject.AddComponent<Predicted2DPhysics>();
                    _senderPhysics = senderPhysics;
                    _senderVerified2D = AttachPhysics(senderPhysics, _sender, physicsId,
                        FullState2D(Array.Empty<SeedEvent>()), "<physics2d>k__BackingField");
                    var receiverPhysics = _receiverPhysicsObject.AddComponent<Predicted2DPhysics>();
                    AttachPhysics(receiverPhysics, _receiver, physicsId,
                        FullState2D(Array.Empty<SeedEvent>()), "<physics2d>k__BackingField");
                }
                else
#endif
                {
                    var senderPhysics = _senderPhysicsObject.AddComponent<Predicted3DPhysics>();
                    _senderPhysics = senderPhysics;
                    _senderVerified = AttachPhysics(senderPhysics, _sender, physicsId,
                        FullState(false), "<physics3d>k__BackingField");
                    var receiverPhysics = _receiverPhysicsObject.AddComponent<Predicted3DPhysics>();
                    AttachPhysics(receiverPhysics, _receiver, physicsId,
                        FullState(false), "<physics3d>k__BackingField");
                }
                _hierarchy = _hierarchyObject.AddComponent<PredictedHierarchy>();
                Set(typeof(PredictionManager), _sender, "<hierarchy>k__BackingField", _hierarchy);
                var hierarchyHistory = _sender.GetVerifiedHistory<FULL_STATE<PredictedHierarchyState>>(
                    _hierarchy.id, out _);
                Set(typeof(PredictedIdentity<PredictedHierarchyState>), _hierarchy, "_verifiedHistory", hierarchyHistory);
                for (ulong tick = BaselineTick; tick <= FinalTick; tick++)
                    hierarchyHistory.Write(tick, default);

                for (ulong tick = CollisionTick; tick <= FinalTick; tick++)
                    SetEvents(tick, tick == CollisionTick ? new[] { SeedEvent.Collision(4) } : Array.Empty<SeedEvent>());

#if UNITY_PHYSICS_2D
                if (use2D)
                {
                    var callbacks = _callbackObject.AddComponent<PredictedRigidbody2D>();
                    _callbackObject.GetComponent<Rigidbody2D>().simulated = false;
                    AttachCallbacks(callbacks);
                    callbacks.onPredictedCollisionEnter += c => Record("collision/Enter", c.contacts[0].point.x);
                    callbacks.onPredictedCollisionStay += c => Record("collision/Stay", c.contacts[0].point.x);
                    callbacks.onPredictedCollisionExit += c => Record("collision/Exit", c.contacts[0].point.x);
                    callbacks.onPredictedTriggerEnter += _ => Record("trigger/Enter", 0);
                    callbacks.onPredictedTriggerStay += _ => Record("trigger/Stay", 0);
                    callbacks.onPredictedTriggerExit += _ => Record("trigger/Exit", 0);
                }
                else
#endif
                {
                    var callbacks = _callbackObject.AddComponent<PredictedPhysicsCallbacks>();
                    AttachCallbacks(callbacks);
                    callbacks.onPredictedCollisionEnter += c => Record("collision/Enter", c.collision.relativeVelocity.magnitude);
                    callbacks.onPredictedCollisionStay += c => Record("collision/Stay", c.collision.relativeVelocity.magnitude);
                    callbacks.onPredictedCollisionExit += c => Record("collision/Exit", c.collision.relativeVelocity.magnitude);
                    callbacks.onPredictedTriggerEnter += _ => Record("trigger/Enter", 0);
                    callbacks.onPredictedTriggerStay += _ => Record("trigger/Stay", 0);
                    callbacks.onPredictedTriggerExit += _ => Record("trigger/Exit", 0);
                }
            }

            private void AttachCallbacks(PredictedIdentity callbacks)
            {
                _callbackIdentity = callbacks;
                callbacks.id = _callbackId;
                Set(typeof(PredictedIdentity), callbacks, "<predictionManager>k__BackingField", _receiver);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager), _receiver, "_instanceMap").Add(_callbackId, callbacks);
            }

            private void Record(string kind, float marker)
            {
                callbackTicks.Add(_receiver.localTickInContext);
                callbackSpeeds.Add(marker);
                callbackKinds.Add(kind);
                callbackWasVerifiedReplay.Add(_receiver.isVerifiedAndReplaying);
                afterCallback?.Invoke();
            }

            public ulong VerifiedTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_verifiedServerTick");

            public ulong AcknowledgedTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_ackedServerTick");

            public ulong LatestAcceptedFrameTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_latestFrameServerTick");

            public ulong InputMarginFeedbackTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_frameInputMarginTick");

            public ulong InputSlackFeedbackTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_frameInputSlackServerTick");

            public ulong PredictionHead => _receiver.localTick;

            public bool IsReplayingOrSimulating => _receiver.isReplaying || _receiver.isSimulating;

            public bool CallbackEndpointIsRegistered => Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                typeof(PredictionManager), _receiver, "_instanceMap").ContainsKey(_callbackId);

            public void UnregisterCallbackEndpoint() => _receiver.UnregisterInstance(_callbackIdentity);

            public bool RecordFailure => Get<bool>(
                typeof(PredictionManager), _receiver, "_frameApplyHadRecordFailure");

            public bool RequiresCheckpoint => Get<bool>(
                typeof(PredictionManager), _receiver, "_awaitingFullFrame");

            public bool HistoryResyncPending => Get<bool>(
                typeof(PredictionManager), _receiver, "_historyResyncPending");

            public ulong RequiredResyncTick => Get<ulong>(
                typeof(PredictionManager), _receiver, "_historyResyncRequiredAfterTick");

            public int AuthoritativeCollisionCount
            {
                get
                {
                    Assert.That(_senderVerified.TryGet(CollisionTick, out var snapshot), Is.True);
                    return snapshot.state.events.Count;
                }
            }

            public void ReplaceHistoricalCollision(bool includeCollision) =>
                SetEvents(CollisionTick, includeCollision ? new[] { SeedEvent.Collision(4) } : Array.Empty<SeedEvent>());

            public void SetEvents(ulong tick, params SeedEvent[] events)
            {
                // Keep the empty topology fixture defined at extended window-boundary ticks.
                Get<History<FULL_STATE<PredictedHierarchyState>>>(
                    typeof(PredictedIdentity<PredictedHierarchyState>), _hierarchy, "_verifiedHistory")
                    .Write(tick, default);
#if UNITY_PHYSICS_2D
                if (_use2D)
                {
                    _senderVerified2D.Write(tick, FullState2D(events));
                    return;
                }
#endif
                _senderVerified.Write(tick, FullState(events));
            }

            public void ClearSenderEventHistory()
            {
#if UNITY_PHYSICS_2D
                if (_use2D)
                {
                    _senderVerified2D.Clear();
                    return;
                }
#endif
                _senderVerified.Clear();
            }

            public bool HasExactEventTick(ulong tick)
            {
#if UNITY_PHYSICS_2D
                if (_use2D)
                    return _senderVerified2D.TryGet(tick, out _);
#endif
                return _senderVerified.TryGet(tick, out _);
            }

            public bool HasPreviousEventState(ulong tick)
            {
#if UNITY_PHYSICS_2D
                if (_use2D)
                    return _senderVerified2D.ReadOrPrevious(tick, out _);
#endif
                return _senderVerified.ReadOrPrevious(tick, out _);
            }

            public void CaptureEvents(ulong tick, params SeedEvent[] events)
            {
                Set(typeof(PredictionManager), _sender, "<localTick>k__BackingField", tick);
#if UNITY_PHYSICS_2D
                if (_use2D)
                {
                    var physics = (Predicted2DPhysics)_senderPhysics;
                    physics.fullPredictedState.Dispose();
                    physics.fullPredictedState = FullState2D(events);
                }
                else
#endif
                {
                    var physics = (Predicted3DPhysics)_senderPhysics;
                    physics.fullPredictedState.Dispose();
                    physics.fullPredictedState = FullState(events);
                }
                Invoke(_sender, "CapturePhysicsEventState", tick);
            }

            public void SetVisible(ulong tick, bool visible) =>
                _visibility.SetVisible(tick, _callbackId.objectId, visible);

            public void SetReceiverVerifiedTick(ulong tick)
            {
                Set(typeof(PredictionManager), _receiver, "_verifiedServerTick", tick);
                Set(typeof(PredictionManager), _receiver, "_latestFrameServerTick", tick);
            }

            public void SetReceiverPredictionHead(ulong tick)
            {
                Set(typeof(PredictionManager), _receiver, "<localTick>k__BackingField", tick);
                Set(typeof(PredictionManager), _receiver, "<localTickInContext>k__BackingField", tick);
            }

            public void SetReceiverPendingHistoryResync(ulong acknowledgedTick)
            {
                Set(typeof(PredictionManager), _receiver, "_ackedServerTick", acknowledgedTick);
                // Model an already outstanding request. The mock manager has no transport;
                // this suppresses only the duplicate RPC, not the real queued-frame guard.
                Set(typeof(PredictionManager), _receiver, "_nextHistoryResyncRequestAt", double.PositiveInfinity);
            }

            public void SetTickRate(int rate)
            {
                foreach (var manager in new[] { _sender, _receiver })
                {
                    Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", rate);
                    Set(typeof(PredictionManager), manager, "<tickDelta>k__BackingField", 1f / rate);
                }
            }

            public void SetReceiverTickRate(int rate)
            {
                Set(typeof(PredictionManager), _receiver, "<tickRate>k__BackingField", rate);
                Set(typeof(PredictionManager), _receiver, "<tickDelta>k__BackingField", 1f / rate);
            }

            public int InputCacheCapacity => ((Array)Get<object>(
                typeof(PredictionManager), _sender, "_inputBlockCache")).Length;

            public void CacheInputTick(ulong tick) => Invoke(_sender, "CaptureInputHistory", tick);

            public bool HasCachedInputTick(ulong tick)
            {
                var cache = (Array)Get<object>(typeof(PredictionManager), _sender, "_inputBlockCache");
                if (cache == null)
                    return false;
                var slot = cache.GetValue((int)(tick % (ulong)cache.Length));
                var slotType = slot.GetType();
                return (ulong)slotType.GetField("tick").GetValue(slot) == tick &&
                       slotType.GetField("packer").GetValue(slot) != null;
            }

            public bool PrepareOutboundFullFrame(ulong tick, ulong baselineTick)
            {
                var result = PrepareOutboundFrame(tick, baselineTick, default);
                Assert.That(result.preparedTick, Is.EqualTo(tick));
                return result.fullFrame;
            }

            public OutboundSelection PrepareOutboundFrame(ulong tick, ulong baselineTick,
                PlayerPacker initialFrame, bool includePhysics = true)
            {
                Set(typeof(PredictionManager), _sender, "<localTick>k__BackingField", tick);
                var frames = Get<List<PlayerPacker>>(typeof(PredictionManager), _sender, "_clientFrames");
                var queues = Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager), _sender, "_clientTicks");
                var player = default(PlayerID);
                Assert.That(frames, Is.Empty);
                queues.Add(player, new PredictionManager.InputQueue { ackedServerTick = baselineTick });
                using var packer = BitPackerPool.Get();
                Packer<uint>.Write(packer, 0xD00DFEEDu);
                initialFrame.player = player;
                initialFrame.packer = packer;
                // Frames are ordered and reliable: the last frame sent is the next frame's baseline.
                initialFrame.lastSentFrameTick = Math.Max(initialFrame.lastSentFrameTick, baselineTick);
                frames.Add(initialFrame);
                var physics3D = Get<Predicted3DPhysics>(typeof(PredictionManager), _sender, "<physics3d>k__BackingField");
                var physics2D = Get<Predicted2DPhysics>(typeof(PredictionManager), _sender, "<physics2d>k__BackingField");
                if (!includePhysics)
                {
                    Set(typeof(PredictionManager), _sender, "<physics3d>k__BackingField", null);
                    Set(typeof(PredictionManager), _sender, "<physics2d>k__BackingField", null);
                }
                // This test isolates the event coverage decision. The fixture hierarchy has
                // no real prefab graph; the normal no-hierarchy writer is sufficient here.
                Set(typeof(PredictionManager), _sender, "<hierarchy>k__BackingField", null);
                try
                {
                    // Explicitly prepare the no-hierarchy input world for every unsent
                    // server tick before selecting this frame's delivery mode.
                    for (ulong prepared = _lastOutboundPreparedTick + 1; prepared <= tick; prepared++)
                    {
                        Set(typeof(PredictionManager), _sender, "<localTick>k__BackingField", prepared);
                        Invoke(_sender, "CaptureInputHistory", prepared);
                        Invoke(_sender, "CaptureLifecycleHistory", prepared);
                        _lastOutboundPreparedTick = prepared;
                    }
                    Set(typeof(PredictionManager), _sender, "<localTick>k__BackingField", tick);
                    Invoke(_sender, "WriteInitialFrameToOthers");
                    return new OutboundSelection(frames[0], baselineTick);
                }
                finally
                {
                    Set(typeof(PredictionManager), _sender, "<hierarchy>k__BackingField", _hierarchy);
                    Set(typeof(PredictionManager), _sender, "<physics3d>k__BackingField", physics3D);
                    Set(typeof(PredictionManager), _sender, "<physics2d>k__BackingField", physics2D);
                    frames.Clear();
                    queues.Remove(player);
                }
            }

            public EventSection WriteMalformedEventSection(string corruption)
            {
                var packer = BitPackerPool.Get();
                try
                {
                    if (corruption == "batch-count")
                        Packer<PackedUInt>.Write(packer, checked((uint)_sender.verifiedHistoryWindowTicks + 1));
                    else
                    {
                        Packer<PackedUInt>.Write(packer, 2u);
                        ulong firstTick = corruption == "descending-tick" ? 12UL : 11UL;
                        Packer<PackedUInt>.Write(packer, (uint)(firstTick - BaselineTick));
                        WriteHistoricalRecord(packer, firstTick);

                        uint offset = corruption switch
                        {
                            "zero-tick" => 0u,
                            "duplicate-tick" => 1u,
                            "descending-tick" => 1u,
                            "current-tick" => 4u,
                            _ => 2u
                        };
                        Packer<PackedUInt>.Write(packer, offset);
                        if (corruption == "record-length")
                        {
                            AddressedPredictionRecords.WriteSectionCount(1, packer);
                            Packer<PredictedComponentID>.Write(packer, _senderPhysics.id);
                            Packer<bool>.Write(packer, true);
                            // The actual received frame ends long before this declared payload.
                            // Pooled packer capacity must not be accepted as received data.
                            Packer<PackedUInt>.Write(packer, 65536u);
                        }
                        else
                            WriteHistoricalRecord(packer, 12, corruption == "record-count" ? 3 : 1);
                    }
                    AddressedPredictionRecords.WriteSectionCount(0, packer);
                    return new EventSection(packer, BaselineTick, false);
                }
                catch
                {
                    packer.Dispose();
                    throw;
                }
            }

            private void WriteHistoricalRecord(BitPacker destination, ulong tick, int recordCount = 1)
            {
                Assert.That(_senderVerified.TryGet(tick, out var snapshot), Is.True);
                using var payload = BitPackerPool.Get();
                Packer<PredictedPhysicsData>.Write(payload, snapshot.state);
                AddressedPredictionRecords.WriteSectionCount(recordCount, destination);
                for (int i = 0; i < recordCount; i++)
                    AddressedPredictionRecords.WriteRecord(destination, _senderPhysics.id, true, payload);
            }

            public EventSection WriteEventSection(ulong tick, ulong baselineTick = BaselineTick, bool fullFrame = false)
            {
                Set(typeof(PredictionManager), _sender, "<localTick>k__BackingField", tick);
                var packer = BitPackerPool.Get();
                try
                {
                    Invoke(_sender, "WritePhysicsEventHistory", default(PlayerID), _visibility,
                        packer, tick, baselineTick, fullFrame);
                    Invoke(_sender, "WriteAddressedStateSection", default(PlayerID), _visibility,
                        packer, tick, baselineTick, fullFrame, true);
                    return new EventSection(packer, baselineTick, fullFrame);
                }
                catch
                {
                    packer.Dispose();
                    throw;
                }
            }

            public void QueueFrame(ulong tick, EventSection events,
                ulong? inputAck = null, int? inputMargin = null, int? inputSlackMs = null)
            {
                var frame = BitPackerPool.Get();
                try
                {
                    if (events.fullFrame)
                    {
                        Packer<PackedInt>.Write(frame, 60);
                        Packer<float>.Write(frame, 1f / 60);
                        Packer<uint>.Write(frame, 123u);
                    }
                    Packer<PackedUInt>.Write(frame, 0u); // visibility deletions
                    Packer<bool>.Write(frame, false); // unchanged topology: no hierarchy record
                    if (!events.fullFrame)
                    {
                        Packer<PackedUInt>.Write(frame, checked((uint)(tick - events.baselineTick)));
                        for (ulong inputTick = events.baselineTick + 1; inputTick <= tick; inputTick++)
                            Packer<PackedUInt>.Write(frame, 0u); // complete empty input transcript
                        Packer<bool>.Write(frame, false); // no historical lifecycle hierarchy
                    }
                    AddressedPredictionRecords.WriteSectionCount(0, frame); // regular state
                    if (events.fullFrame)
                        AddressedPredictionRecords.WriteSectionCount(0, frame); // first inputs
                    events.packer.ResetPositionAndMode(true);
                    frame.WriteBits(events.packer, events.bitCount);
                    int bytes = frame.positionInBytes;
                    frame.ResetPositionAndMode(true);
                    Invoke(_receiver, "HandleFrameFromServer", tick, events.baselineTick,
                        inputAck ?? tick, events.fullFrame,
                        inputMargin.HasValue, (PackedInt)(inputMargin ?? 0),
                        inputSlackMs.HasValue, (PackedInt)(inputSlackMs ?? 0),
                        new BitPackerWithLength(bytes, frame));
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }

            public void Reconcile() => Invoke(_receiver, "Update");

            private History<FULL_STATE<TState>> AttachPhysics<TState>(
                PredictedIdentity<TState> physics, PredictionManager manager, PredictedComponentID id,
                FULL_STATE<TState> initial, string physicsField) where TState : struct, IPredictedData<TState>
            {
                physics.id = id;
                Set(typeof(PredictedIdentity), physics, "<predictionManager>k__BackingField", manager);
                Set(typeof(PredictedIdentity<TState>), physics, "myType", physics.GetType());
                physics.fullPredictedState = initial;
                var predicted = new History<FULL_STATE<TState>>(600);
                predicted.Write(BaselineTick, initial.DeepCopy());
                Set(typeof(PredictedIdentity<TState>), physics, "_stateHistory", predicted);
                var verified = manager.GetVerifiedHistory<FULL_STATE<TState>>(id, out _);
                verified.Write(BaselineTick, initial.DeepCopy());
                Set(typeof(PredictedIdentity<TState>), physics, "_verifiedHistory", verified);
                physics.lastVerifiedTick = BaselineTick;
                Get<List<PredictedIdentity>>(typeof(PredictionManager), manager, "_systems").Add(physics);
                Set(typeof(PredictionManager), manager, "_systemsCount", 1);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager), manager, "_instanceMap").Add(id, physics);
                Set(typeof(PredictionManager), manager, physicsField, physics);
                return verified;
            }

            private FULL_STATE<PredictedPhysicsData> FullState(bool collision) =>
                FullState(collision ? new[] { SeedEvent.Collision(4) } : Array.Empty<SeedEvent>());

            private FULL_STATE<PredictedPhysicsData> FullState(SeedEvent[] events)
            {
                var result = new FULL_STATE<PredictedPhysicsData>
                {
                    state = new PredictedPhysicsData { events = DisposableList<PhysicsEvent>.Create(events.Length) }
                };
                result.prediction.wasOnSimulationStartCalled = true;
                foreach (var seed in events)
                {
                    result.state.events.Add(new PhysicsEvent
                    {
                        type = seed.type,
                        isTrigger = seed.trigger,
                        me = _callbackId,
                        // An untracked scene collider is represented by the supported zero ID.
                        other = default,
                        collision = new PhysicsCollision
                        {
                            relativeVelocity = Vector3.down * seed.marker,
                            contacts = DisposableList<PhysicsContactPoint>.Create(0)
                        }
                    });
                }
                return result;
            }

#if UNITY_PHYSICS_2D
            private FULL_STATE<PredictedPhysics2DData> FullState2D(SeedEvent[] events)
            {
                var result = new FULL_STATE<PredictedPhysics2DData>
                {
                    state = new PredictedPhysics2DData { events = DisposableList<Physics2DEvent>.Create(events.Length) }
                };
                result.prediction.wasOnSimulationStartCalled = true;
                foreach (var seed in events)
                {
                    var contacts = DisposableList<Physics2DContactPoint>.Create(1);
                    contacts.Add(new Physics2DContactPoint { point = new Vector2(seed.marker, 0) });
                    result.state.events.Add(new Physics2DEvent
                    {
                        type = seed.type,
                        isTrigger = seed.trigger,
                        me = _callbackId,
                        other = default,
                        contacts = contacts
                    });
                }
                return result;
            }
#endif

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                var queue = Get<object>(typeof(PredictionManager), _receiver, "_deltas");
                foreach (IDisposable frame in (IEnumerable)queue)
                    frame.Dispose();
                queue.GetType().GetMethod("Clear").Invoke(queue, null);
                Set(typeof(NetworkIdentity), _receiver, "_isSpawnedClient", false);
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                // Detach the synthetic hierarchy before identity teardown: it has no live
                // topology and exists only to select the real event projection writer.
                Set(typeof(PredictionManager), _sender, "<hierarchy>k__BackingField", null);
                // EditMode objects created solely for a test may never receive OnDestroy.
                // Release owned live/local-history collections explicitly in either case.
                _senderPhysics.ReleasePredictionStateForPool();
                _receiverPhysicsObject.GetComponent<PredictedIdentity>().ReleasePredictionStateForPool();
                _hierarchy.ReleasePredictionStateForPool();
                Object.DestroyImmediate(_callbackObject);
                Object.DestroyImmediate(_senderPhysicsObject);
                Object.DestroyImmediate(_receiverPhysicsObject);
                Invoke(_sender, "ClearVerifiedStores");
                Invoke(_receiver, "ClearVerifiedStores");
                Invoke(_receiver, "ClearCheckpointDelivery");
                Invoke(_sender, "DisposeInputBlockCache");
                Invoke(_sender, "DisposeLifecycleHistory");
                Object.DestroyImmediate(_hierarchyObject);
                Object.DestroyImmediate(_senderObject);
                Object.DestroyImmediate(_receiverObject);
                Object.DestroyImmediate(_networkObject);
            }
        }

        private static PredictionManager CreateManager(GameObject gameObject)
        {
            var manager = gameObject.AddComponent<PredictionManager>();
            Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 60);
            Set(typeof(PredictionManager), manager, "<tickDelta>k__BackingField", 1f / 60);
            Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 18UL);
            Set(typeof(PredictionManager), manager, "<localTickInContext>k__BackingField", 18UL);
            Set(typeof(PredictionManager), manager, "_physicsProvider", default(PredictionPhysicsProvider));
            Set(typeof(PredictionManager), manager, "_updateViewMode", UpdateViewMode.None);
            return manager;
        }

        private static T Get<T>(Type type, object target, string name) =>
            (T)Field(type, name).GetValue(target);

        private static void Set(Type type, object target, string name, object value) =>
            Field(type, name).SetValue(target, value);

        private static FieldInfo Field(Type type, string name)
        {
            var field = type.GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return field;
        }

        private static void Invoke(PredictionManager manager, string name, params object[] arguments)
        {
            var method = typeof(PredictionManager).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            method.Invoke(manager, arguments);
        }
    }
#endif
}
