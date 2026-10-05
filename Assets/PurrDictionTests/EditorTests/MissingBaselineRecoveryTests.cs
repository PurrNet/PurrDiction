using System;
using System.Collections;
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
    // Request delivery and reliable-send bookkeeping are simulated. The addressed codec,
    // client frame drain, server selection, request handler and input ACK handler are real.
    public sealed class MissingBaselineRecoveryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(RecoveryProbeState));
            Packer<RecoveryProbeState>.RegisterWriter((packer, state) => Packer<int>.Write(packer, state.value));
            Packer<RecoveryProbeState>.RegisterReader((BitPacker packer, ref RecoveryProbeState state) =>
                state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void MissingBaselineRetriesLostRequestThenAFullRecoversAndCoversLateRequests()
        {
            using var f = new Fixture();
            f.RemoveClientBaseline();
            Assert.That(f.PrepareAndQueue(11, 110), Is.False);
            f.ExpectMissingBaseline();
            f.Drain();
            Assert.That(f.Pending, Is.True);
            Assert.That(f.RequiredTick, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.clientProbe.deltaReads, Is.Zero,
                "a real absent baseline must be recognized before the custom delta codec runs");

            Assert.That(f.TakeRequest(100), Is.True); // drop the first request
            Assert.That(f.ServerFrame.fullFrame, Is.False);
            Assert.That(f.TakeRequest(100.5), Is.False);
            Assert.That(f.TakeRequest(101), Is.True,
                "a pending request must remain retryable with no additional incoming frames");

            f.RecordRecentOptionalDesyncResync();
            f.DeliverRequest();
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.True,
                "the first explicit baseline fault must not be blocked by optional desync-healing cooldown");
            Assert.That(f.PrepareAndQueue(12, 120), Is.True,
                "the missing baseline must request a full frame on the next server tick");

            f.Drain();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.RequiredTick, Is.Zero);
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.VerifiedValue(12), Is.EqualTo(120));
            Assert.That(f.TakeRequest(200), Is.False);

            // A request that was still in flight when the full was sent is already covered by it.
            f.ExpireServerRequestCooldown();
            f.DeliverRequest(11);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.False,
                "the full at 12 follows the failed tick on the ordered stream");
            Assert.That(f.PrepareAndQueue(13, 130), Is.False);
            f.Drain();
            Assert.That(f.VerifiedValue(13), Is.EqualTo(130));
            Assert.That(f.ClientAck, Is.EqualTo(13));
            Assert.That(f.Pending, Is.False);
        }

        [Test]
        public void ValidDeltaKeepsNormalAcknowledgementWithoutRequestingFull()
        {
            using var f = new Fixture();
            Assert.That(f.PrepareAndQueue(11, 110), Is.False);
            f.Drain();
            Assert.That(f.VerifiedValue(11), Is.EqualTo(110));
            Assert.That(f.ClientAck, Is.EqualTo(11));
            Assert.That(f.clientProbe.deltaReads, Is.EqualTo(1));
            Assert.That(f.Pending, Is.False);
            Assert.That(f.TakeRequest(100), Is.False);
        }

        [Test]
        public void OmittedUnchangedStateWithMissingBaselineAlsoRequestsRecovery()
        {
            using var f = new Fixture();
            f.RemoveClientBaseline();
            f.PrepareAndQueue(11, 80); // equals baseline 8; normal writer omits the record
            Assert.That(f.LastRegularRecordCount, Is.Zero);
            f.ExpectMissingBaseline();
            f.Drain();
            Assert.That(f.Pending, Is.True);
            Assert.That(f.RequiredTick, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.clientProbe.deltaReads, Is.Zero);
            Assert.That(f.TakeRequest(100), Is.True);
        }

        [Test]
        public void SenderWithoutItsOwnBaselineUsesFullIdentityRecordInsideTheDeltaFrame()
        {
            using var f = new Fixture();
            f.RemoveServerBaseline();
            Assert.That(f.PrepareAndQueue(11, 110), Is.False);
            Assert.That(f.LastRegularRecordFullFlags, Is.EqualTo(new[] { true }),
                "the sender must not emit a default-based delta when its own state baseline is absent");
            f.Drain();
            Assert.That(f.VerifiedValue(11), Is.EqualTo(110));
            Assert.That(f.clientProbe.deltaReads, Is.Zero);
            Assert.That(f.ClientAck, Is.EqualTo(11));
            Assert.That(f.Pending, Is.False);
        }

        [Test]
        public void CompleteInputTranscriptDoesNotDependOnAReceiverInputBaseline()
        {
            using var f = new Fixture();
            f.EnableInputWithServerBaseline();
            f.PrepareAndQueue(11, 110);
            Assert.That(f.LastInputTranscriptTicks, Is.EqualTo(3),
                "the actual input writer must include every tick after baseline 8");
            f.Drain();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(11));
            Assert.That(f.VerifiedValue(11), Is.EqualTo(110));
            Assert.That(f.clientProbe.inputReads, Is.GreaterThan(0));
            Assert.That(f.clientProbe.lastInput, Is.EqualTo(42));
            Assert.That(f.TakeRequest(100), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GenericDecoderAndRosterFailuresDoNotStartAutomaticResyncLoops(bool rosterMismatch)
        {
            using var f = new Fixture();
            f.clientProbe.readFailure = rosterMismatch
                ? new PredictedModuleRosterMismatchException("recovery test roster mismatch")
                : new InvalidOperationException("recovery test generic decode failure");
            LogAssert.Expect(LogType.Error, new Regex(rosterMismatch
                ? "recovery test roster mismatch.*State replication"
                : "Discarded prediction record.*recovery test generic decode failure"));
            f.PrepareAndQueue(11, 110);
            f.Drain();
            Assert.That(f.clientProbe.deltaReads, Is.EqualTo(1));
            Assert.That(f.Pending, Is.False);
            Assert.That(f.TakeRequest(100), Is.False);
            Assert.That(f.ClientAck, Is.EqualTo(rosterMismatch ? 11UL : 10UL));
        }

        [Test]
        public void PendingRequestRequiresSuccessfulFullCoveringTheNewestFault()
        {
            using var f = new Fixture();
            f.MarkPending(12);
            f.MarkPending(14);
            Assert.That(f.RequiredTick, Is.EqualTo(14));
            Assert.That(f.TakeRequest(100), Is.True);

            f.PrepareAndQueue(11, 110);
            f.Drain();
            Assert.That(f.Pending, Is.True, "a successful delta cannot complete a requested full-state replacement");
            f.DeliverAck();

            f.PrepareAndQueue(12, 120, forceFull: true);
            f.Drain();
            Assert.That(f.Pending, Is.True, "a successful but older full frame cannot cover fault 14");
            f.DeliverAck();

            f.clientProbe.failFullState = true;
            f.clientProbe.readFailure = new InvalidOperationException("recovery test full decode failure");
            LogAssert.Expect(LogType.Error, new Regex("Discarded prediction record.*recovery test full decode failure"));
            f.PrepareAndQueue(14, 140, forceFull: true);
            f.Drain();
            Assert.That(f.Pending, Is.True, "merely receiving a covering full frame is not recovery");
            Assert.That(f.RequiredTick, Is.EqualTo(14));
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.TakeRequest(101), Is.True);

            f.clientProbe.failFullState = false;
            f.clientProbe.readFailure = null;
            f.ExpireServerRequestCooldown();
            f.DeliverRequest(); // explicit rejection proof releases the failed full's credit
            f.PrepareAndQueue(15, 150, forceFull: true);
            f.Drain();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.ClientAck, Is.EqualTo(15));
            Assert.That(f.VerifiedValue(15), Is.EqualTo(150));
        }

        [Test]
        public void CleanupClearsOutstandingRequestAndItsRetryEpoch()
        {
            using var f = new Fixture();
            f.MarkPending(11);
            Assert.That(f.TakeRequest(100), Is.True);
            f.CleanupClient();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.RequiredTick, Is.Zero);
            Assert.That(f.TakeRequest(1000), Is.False);
            Assert.That(Get<double>(f.client, "_nextHistoryResyncRequestAt"), Is.Zero);
        }

        [TestCase("prepare", false)]
        [TestCase("simulate", false)]
        [TestCase("beforePhysics", false)]
        [TestCase("afterPhysics", false)]
        [TestCase("late", false)]
        [TestCase("post", false)]
        [TestCase("latestUnity", false)]
        [TestCase("prepare", true)]
        [TestCase("simulate", true)]
        [TestCase("beforePhysics", true)]
        [TestCase("afterPhysics", true)]
        [TestCase("late", true)]
        [TestCase("post", true)]
        [TestCase("latestUnity", true)]
        public void AuthoritativeCallbackFailureDoesNotVerifyOrAcknowledgeTheFrame(string phase, bool full)
        {
            using var f = new Fixture();
            ulong failedTick = full ? 12UL : 11UL;
            if (full)
            {
                f.PrepareAndQueue(11, 110);
                f.Drain();
            }
            ulong verifiedBefore = f.ClientVerifiedTick;
            f.EnableCallbackFailure(phase, failedTick);
            f.PrepareAndQueue(failedTick, (int)failedTick * 10, forceFull: full);
            f.Drain();

            f.AssertInjectedCallbackLog();
            Assert.That(f.clientProbe.callbackFailures, Is.EqualTo(1), "the intended callback must actually throw");
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(verifiedBefore), "a partially simulated frame is not verified");
            Assert.That(f.ClientAck, Is.EqualTo(verifiedBefore), "a partially simulated frame must not be ACKed");
            Assert.That(f.Pending, Is.True);
            Assert.That(f.RequiredTick, Is.EqualTo(failedTick));
            f.AssertCallbackPassStoppedAt(failedTick, phase);
            f.AssertIdleContext();

            f.clientProbe.callbackFailurePhase = null;
            int delivered = f.clientProbe.callbackObservations.Count;
            f.PrepareAndQueue(failedTick + 1, ((int)failedTick + 1) * 10);
            f.Drain();
            Assert.That(f.clientProbe.callbackObservations.Count, Is.EqualTo(delivered),
                "a delta continuing the failed frame must not repeat already delivered callbacks");
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(verifiedBefore));
            Assert.That(f.ClientAck, Is.EqualTo(verifiedBefore));

            f.DeliverRequest();
            ulong recoveryTick = failedTick + 2;
            Assert.That(f.PrepareAndQueue(recoveryTick, (int)recoveryTick * 10), Is.True,
                "recovery must use an actual server-prepared checkpoint");
            f.Drain();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(recoveryTick));
            Assert.That(f.ClientAck, Is.EqualTo(recoveryTick));
            Assert.That(f.VerifiedValue(recoveryTick), Is.EqualTo((int)recoveryTick * 10));
            f.AssertCallbackPassStoppedAt(failedTick, phase);
            f.DeliverAck();
            Assert.That(f.PrepareAndQueue(recoveryTick + 1, ((int)recoveryTick + 1) * 10), Is.False);
            f.Drain();
            Assert.That(f.ClientAck, Is.EqualTo(recoveryTick + 1));
            Assert.That(f.ServerAck, Is.EqualTo(recoveryTick));
            Assert.That(f.Pending, Is.False);
            f.AssertIdleContext();
            f.AssertInjectedCallbackLog();
        }

        [TestCase("prepare")]
        [TestCase("simulate")]
        [TestCase("beforePhysics")]
        [TestCase("afterPhysics")]
        [TestCase("late")]
        [TestCase("post")]
        [TestCase("latestUnity")]
        public void CallbackFailureDuringGapRequiresCheckpointWithoutRepeatingEarlierTicks(string phase)
        {
            using var f = new Fixture();
            f.EnableCallbackFailure(phase, 12);
            Assert.That(f.PrepareAndQueue(14, 140), Is.False);
            f.Drain();
            f.AssertInjectedCallbackLog();
            Assert.That(f.clientProbe.callbackFailures, Is.EqualTo(1));
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(10));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.RequiredTick, Is.EqualTo(14));
            f.AssertCallbackPassStoppedAt(11, "latestUnity");
            f.AssertCallbackPassStoppedAt(12, phase);
            Assert.That(f.clientProbe.CallbacksAt(13), Is.Empty, "the failing gap pass must stop the transcript");
            Assert.That(f.clientProbe.CallbacksAt(14), Is.Empty);
            f.AssertIdleContext();

            f.clientProbe.callbackFailurePhase = null;
            int delivered = f.clientProbe.callbackObservations.Count;
            f.PrepareAndQueue(15, 150);
            f.Drain();
            Assert.That(f.clientProbe.callbackObservations.Count, Is.EqualTo(delivered));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            f.DeliverRequest();
            Assert.That(f.PrepareAndQueue(16, 160), Is.True);
            f.Drain();
            Assert.That(f.Pending, Is.False);
            Assert.That(f.ClientAck, Is.EqualTo(16));
            Assert.That(f.VerifiedValue(16), Is.EqualTo(160));
            f.AssertCallbackPassStoppedAt(11, "latestUnity");
            f.AssertCallbackPassStoppedAt(12, phase);
            f.DeliverAck();
            Assert.That(f.PrepareAndQueue(17, 170), Is.False);
            f.Drain();
            Assert.That(f.ClientAck, Is.EqualTo(17));
            f.AssertIdleContext();
            f.AssertInjectedCallbackLog();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonverifiedCallbackFailureRetainsLocalLogAndContinueBehavior(bool serverLocal)
        {
            using var f = new Fixture();
            f.EnableCallbackFailure("simulate", 11, verifiedOnly: false);
            f.RunNonverifiedPass(11, serverLocal);
            f.AssertInjectedCallbackLog();
            Assert.That(f.clientProbe.callbackFailures, Is.EqualTo(1));
            f.AssertCallbackPassStoppedAt(11, "latestUnity");
            Assert.That(f.Pending, Is.False);
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(10));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            f.AssertIdleContext();
        }

        [Test]
        public void PersistentCallbackFailureFallsBackToLoggedContinuationAfterRepeatedCheckpointRejections()
        {
            using var f = new Fixture();
            f.EnableCallbackFailure("simulate", 0);
            f.clientProbe.callbackFailureAnyTick = true;
            int limit = PredictionManager.MaxRejectedCheckpointsBeforeTolerance;
            ulong tick = 11;
            for (int rejected = 1; rejected <= limit; rejected++, tick++)
            {
                if (rejected > 1)
                {
                    f.ExpireServerRequestCooldown();
                    f.DeliverRequest();
                }
                Assert.That(f.PrepareAndQueue(tick, (int)tick * 10, forceFull: rejected == 1), Is.True,
                    $"replacement {rejected} must be a server-prepared checkpoint");
                f.Drain();
                Assert.That(f.ClientVerifiedTick, Is.EqualTo(10), $"rejected checkpoint {rejected} must not verify");
                Assert.That(f.ClientAck, Is.EqualTo(10));
                Assert.That(f.Pending, Is.True);
            }
            Assert.That(f.clientProbe.callbackFailures, Is.EqualTo(limit));
            Assert.That(f.client.toleratedReplayHookFailuresTotal, Is.Zero);

            // A deterministic client-side fault would otherwise reject every replacement
            // forever, with the live world already overwritten by each failed snapshot.
            f.ExpireServerRequestCooldown();
            f.DeliverRequest();
            Assert.That(f.PrepareAndQueue(tick, (int)tick * 10), Is.True);
            f.Drain();
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(tick),
                "after repeated rejections the checkpoint applies with the callback failure logged");
            Assert.That(f.ClientAck, Is.EqualTo(tick));
            Assert.That(f.Pending, Is.False);
            Assert.That(f.client.toleratedReplayHookFailuresTotal, Is.EqualTo(1));
            f.AssertIdleContext();

            f.DeliverAck();
            tick++;
            Assert.That(f.PrepareAndQueue(tick, (int)tick * 10), Is.False, "ordinary deltas resume");
            f.Drain();
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(tick), "the fallback persists while the fault persists");
            Assert.That(f.ClientAck, Is.EqualTo(tick));
            Assert.That(f.client.toleratedReplayHookFailuresTotal, Is.EqualTo(2));

            f.clientProbe.callbackFailureAnyTick = false;
            f.clientProbe.callbackFailurePhase = null;
            f.DeliverAck();
            tick++;
            Assert.That(f.PrepareAndQueue(tick, (int)tick * 10), Is.False);
            f.Drain();
            Assert.That(f.ClientVerifiedTick, Is.EqualTo(tick));
            Assert.That(f.VerifiedValue(tick), Is.EqualTo((int)tick * 10));
            Assert.That(Get<int>(f.client, "_consecutiveRejectedCheckpoints"), Is.Zero,
                "a frame that runs every callback cleanly ends the fallback");
            Assert.That(f.client.toleratedReplayHookFailuresTotal, Is.EqualTo(2));
            f.AssertIdleContext();
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _networkObject = new("Recovery test client network");
            private readonly GameObject _clientObject = new("Recovery test client");
            private readonly GameObject _serverObject = new("Recovery test server");
            private readonly GameObject _clientProbeObject = new("Recovery client identity");
            private readonly GameObject _serverProbeObject = new("Recovery server identity");
            private readonly NetworkManager _network;
            private readonly RecoveryProbeIdentity _serverProbe;
            private readonly PredictionManager _server;
            private readonly History<FULL_STATE<RecoveryProbeState>> _clientVerified;
            private readonly History<FULL_STATE<RecoveryProbeState>> _serverVerified;
            private readonly List<PlayerPacker> _frames;
            private readonly PredictionManager.InputQueue _input;
            private readonly double _previousCadence;
            private ulong _lastPreparedTick = 8;
            private readonly PlayerID _player = default;
            public readonly PredictionManager client;
            public readonly RecoveryProbeIdentity clientProbe;
            public int LastRegularRecordCount { get; private set; }
            public uint LastInputTranscriptTicks { get; private set; }
            public readonly List<bool> LastRegularRecordFullFlags = new();
            private readonly List<string> _callbackErrors = new();
            private bool _capturingCallbackErrors, _previousIgnoreFailingMessages;
            private string _expectedCallbackFailure;
            private Action _beforePhysics, _afterPhysics;

            public Fixture()
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = _networkObject.AddComponent<NetworkManager>();
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                client = CreateManager(_clientObject);
                _server = CreateManager(_serverObject);
                Set(typeof(NetworkIdentity), client, "<networkManager>k__BackingField", _network);
                // Deliberately unspawned: frame drains run directly, and natural request
                // gates are exercised without invoking generated RPCs on a fake transport.
                Set(typeof(NetworkIdentity), client, "_isSpawnedClient", false);
                Set(client, "_verifiedServerTick", 10UL);
                Set(client, "_latestFrameServerTick", 10UL);
                Set(client, "_ackedServerTick", 10UL);
                Set(client, "_awaitingFullFrame", false);
                Set(_server, "<cachedIsServer>k__BackingField", true);
                var id = new PredictedComponentID(new PredictedObjectID(2711), 0);
                clientProbe = _clientProbeObject.AddComponent<RecoveryProbeIdentity>();
                _clientVerified = Attach(clientProbe, client, id);
                _serverProbe = _serverProbeObject.AddComponent<RecoveryProbeIdentity>();
                _serverVerified = Attach(_serverProbe, _server, id);
                _frames = Get<List<PlayerPacker>>(_server, "_clientFrames");
                _frames.Add(new PlayerPacker { player = _player, packer = BitPackerPool.Get(),
                    lastFullFrameSentTick = 8, lastSentFrameTick = 8 });
                _input = new PredictionManager.InputQueue { ackedServerTick = 8 };
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(_server, "_clientTicks").Add(_player, _input);
            }

            public bool Pending => Get<bool>(client, "_historyResyncPending");
            public ulong RequiredTick => Get<ulong>(client, "_historyResyncRequiredAfterTick");
            public ulong ClientAck => Get<ulong>(client, "_ackedServerTick");
            public ulong ClientVerifiedTick => Get<ulong>(client, "_verifiedServerTick");
            public ulong ServerAck => _input.ackedServerTick;
            public PlayerPacker ServerFrame => _frames[0];
            public void MarkPending(ulong tick) => Invoke(client, "MarkHistoryResyncNeeded", tick);
            public bool TakeRequest(double now) => (bool)Invoke(client, "TryTakeHistoryResyncRequest", now);
            public void DeliverRequest(ulong? failedTick = null) =>
                Invoke(_server, "HandleHistoryResyncRequest", _player, failedTick ?? RequiredTick);
            public void Drain() => Invoke(client, "ProcessQueuedFrames", true);
            public void RemoveClientBaseline() => _clientVerified.ClearPast(10);
            public void RemoveServerBaseline() => _serverVerified.ClearPast(10);
            public void EnableInputWithServerBaseline()
            {
                clientProbe.supplyInput = true;
                _serverProbe.supplyInput = true;
                Set(client, "_inputHistorySystems", 1);
                Set(_server, "_inputHistorySystems", 1);
                Invoke(_server, "CaptureInputHistory", 8UL);
                Invoke(_server, "CaptureLifecycleHistory", 8UL);
            }
            public void ExpectMissingBaseline() => LogAssert.Expect(LogType.Error,
                new Regex("Discarded prediction record.*[Mm]issing.*baseline"));

            public void EnableCallbackFailure(string phase, ulong tick, bool verifiedOnly = true)
            {
                // The old runtime logs Exception; rejection logs Error. Capture both so
                // the negative gate reaches the same semantic assertions, while rejecting
                // every missing, duplicate or unrelated error explicitly below.
                _previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
                LogAssert.ignoreFailingMessages = true;
                _capturingCallbackErrors = true;
                Application.logMessageReceived += CaptureCallbackLog;
                clientProbe.recordCallbacks = true;
                clientProbe.callbackFailurePhase = phase;
                clientProbe.callbackFailureTick = tick;
                clientProbe.callbackFailureVerifiedOnly = verifiedOnly;
                _expectedCallbackFailure = $"injected recovery callback failure [{phase}] at tick {tick}";
                _beforePhysics = () => clientProbe.ObserveCallback("beforePhysics");
                _afterPhysics = () => clientProbe.ObserveCallback("afterPhysics");
                client.onBeforePhysicsPass += _beforePhysics;
                client.onAfterPhysicsPass += _afterPhysics;
            }

            private void CaptureCallbackLog(string message, string stack, LogType type)
            {
                if (type is LogType.Error or LogType.Exception or LogType.Assert)
                    _callbackErrors.Add(message);
            }

            public void AssertInjectedCallbackLog()
            {
                Assert.That(_callbackErrors, Has.Count.EqualTo(1),
                    "the scoped collector must reject missing, duplicate or unrelated error logs");
                StringAssert.Contains(_expectedCallbackFailure, _callbackErrors[0]);
            }

            public void AssertCallbackPassStoppedAt(ulong tick, string lastPhase)
            {
                string[] phases = { "prepare", "simulate", "beforePhysics", "afterPhysics", "late", "post", "latestUnity" };
                int count = Array.IndexOf(phases, lastPhase) + 1;
                Assert.That(count, Is.GreaterThan(0));
                var expected = new string[count];
                Array.Copy(phases, expected, count);
                Assert.That(clientProbe.CallbacksAt(tick), Is.EqualTo(expected),
                    $"tick {tick} must run once and stop immediately after {lastPhase}");
            }

            public void AssertIdleContext()
            {
                Assert.That(client.isVerified, Is.False);
                Assert.That(client.isReplaying, Is.False);
                Assert.That(client.isSimulating, Is.False);
                Assert.That(client.isInPhysicsPass, Is.False);
                Assert.That(client.localTickInContext, Is.EqualTo(client.localTick));
                Assert.That(Get<ulong>(client, "_applyingFrameServerTick"), Is.Zero);
            }

            public void RunNonverifiedPass(ulong tick, bool serverLocal)
            {
                Set(client, "<cachedIsServer>k__BackingField", serverLocal);
                Set(client, "<isReplaying>k__BackingField", !serverLocal);
                try
                {
                    var mode = typeof(PredictionManager).GetNestedType("HistorySaveMode", BindingFlags.NonPublic);
                    Invoke(client, "SimulateFrame", tick, Enum.Parse(mode, "Full"), PredictionPassKind.SpeculativeReplay);
                }
                finally
                {
                    Set(client, "<cachedIsServer>k__BackingField", false);
                    Set(client, "<isReplaying>k__BackingField", false);
                }
            }

            public int VerifiedValue(ulong tick)
            {
                Assert.That(_clientVerified.Read(tick, out var state), Is.True);
                return state.state.value;
            }

            public void RecordRecentOptionalDesyncResync() =>
                Get<Dictionary<PlayerID, ulong>>(_server, "_desyncResyncServedTick")[_player] = _server.localTick;

            public void ExpireServerRequestCooldown() =>
                Get<Dictionary<PlayerID, double>>(_server, "_historyResyncServedAt")[_player] = double.NegativeInfinity;

            public bool TryPrepare(ulong tick, int value, bool forceFull = false)
            {
                // Model preparation of the unsent intervening ticks while their entering
                // roster/state is still current, before assigning the requested tick's state.
                for (ulong prepared = _lastPreparedTick + 1; prepared < tick; prepared++)
                {
                    Set(_server, "<localTick>k__BackingField", prepared);
                    Set(_server, "<localTickInContext>k__BackingField", prepared);
                    Invoke(_server, "CaptureInputHistory", prepared);
                    Invoke(_server, "CaptureLifecycleHistory", prepared);
                }
                Set(_server, "<localTick>k__BackingField", tick);
                Set(_server, "<localTickInContext>k__BackingField", tick);
                _serverProbe.fullPredictedState = State(value);
                Invoke(_server, "CaptureInputHistory", tick);
                Invoke(_server, "CaptureLifecycleHistory", tick);
                _lastPreparedTick = tick;
                var frame = _frames[0];
                if (forceFull)
                {
                    frame.fullFrame = true;
                    frame.requiresFullCheckpoint = true;
                }
                _frames[0] = frame;
                Invoke(_server, "WriteInitialFrameToOthers");
                if (_frames[0].preparedFrameTick != tick)
                    return false;
                Invoke(_server, "WriteEventHandles");
                LastRegularRecordCount = CountRegularRecords(_frames[0]);
                return true;
            }

            public bool PrepareAndQueue(ulong tick, int value, bool forceFull = false)
            {
                Assert.That(TryPrepare(tick, value, forceFull), Is.True, "server unexpectedly withheld a fresh frame");
                var frame = _frames[0];
                bool full = frame.fullFrame;
                ulong baseline = frame.preparedBaselineTick;
                QueuePacket(tick, baseline, full, frame.packer);
                // Mirror only the transport-success bookkeeping from SendPreparedServerFrame;
                // no hand-built state records or direct receiver state writes are used.
                if (full) frame.BeginFullFrame(tick);
                frame.lastSentFrameTick = tick;
                frame.fullFrame = false;
                frame.preparedFrameTick = 0;
                _frames[0] = frame;
                return full;
            }

            private void QueuePacket(ulong tick, ulong baseline, bool full, BitPacker source)
            {
                var received = BitPackerPool.Get();
                try
                {
                    int sourceBytes = source.ToByteData().length;
                    source.ResetPositionAndMode(true);
                    received.WriteBits(source, sourceBytes * 8);
                    int bytes = received.positionInBytes;
                    received.ResetPositionAndMode(true);
                    Invoke(client, "HandleFrameFromServer", tick, baseline, tick, full,
                        false, default(PackedInt), false, default(PackedInt), new BitPackerWithLength(bytes, received));
                }
                catch { received.Dispose(); throw; }
            }

            public void DeliverAck()
            {
                var payload = BitPackerPool.Get();
                Invoke(_server, "ReceivedInput", 0UL, 0u, ClientAck, payload, new RPCInfo { sender = _player });
            }

            public void CleanupClient()
            {
                Get<List<PredictedIdentity>>(client, "_systems").Clear();
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(client, "_instanceMap").Clear();
                Set(client, "_systemsCount", 0);
                Invoke(client, "CleanupAllSystems");
            }

            public void Dispose()
            {
                if (_capturingCallbackErrors)
                {
                    client.onBeforePhysicsPass -= _beforePhysics;
                    client.onAfterPhysicsPass -= _afterPhysics;
                    clientProbe.recordCallbacks = false;
                    Application.logMessageReceived -= CaptureCallbackLog;
                    LogAssert.ignoreFailingMessages = _previousIgnoreFailingMessages;
                }
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                var queue = Get<object>(client, "_deltas");
                foreach (IDisposable frame in (IEnumerable)queue) frame.Dispose();
                queue.GetType().GetMethod("Clear").Invoke(queue, null);
                Invoke(client, "ClearCheckpointDelivery");
                clientProbe.ReleasePredictionStateForPool();
                _serverProbe.ReleasePredictionStateForPool();
                foreach (var frame in _frames) frame.Dispose();
                _frames.Clear();
                Invoke(client, "ClearVerifiedStores");
                Invoke(_server, "ClearVerifiedStores");
                Invoke(_server, "DisposeInputBlockCache");
                Invoke(_server, "DisposeLifecycleHistory");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                Object.DestroyImmediate(_clientProbeObject);
                Object.DestroyImmediate(_serverProbeObject);
                Object.DestroyImmediate(_clientObject);
                Object.DestroyImmediate(_serverObject);
                Object.DestroyImmediate(_networkObject);
            }

            private int CountRegularRecords(PlayerPacker frame)
            {
                var packer = frame.packer;
                int end = packer.positionInBits;
                packer.ResetPositionAndMode(true);
                if (frame.fullFrame)
                {
                    Packer<PackedInt>.Read(packer);
                    Packer<float>.Read(packer);
                    Packer<uint>.Read(packer);
                }
                Assert.That(Packer<PackedUInt>.Read(packer).value, Is.Zero);
                Assert.That(Packer<bool>.Read(packer), Is.False);
                LastInputTranscriptTicks = 0;
                if (!frame.fullFrame)
                {
                    int transcriptStart = packer.positionInBits;
                    LastInputTranscriptTicks = Packer<PackedUInt>.Read(packer).value;
                    Assert.That(LastInputTranscriptTicks,
                        Is.EqualTo(frame.preparedFrameTick - frame.preparedBaselineTick));
                    // Inspect with server-side staging so the receiver's missing baseline remains
                    // untouched. The counter does not apply inputs or retain a verified transcript.
                    packer.SetBitPosition(transcriptStart);
                    Invoke(_server, "ReadInputHistory", packer, frame.preparedFrameTick, frame.preparedBaselineTick, end);
                    Invoke(_server, "ClearVerifiedInputTranscript");
                    Assert.That(Packer<bool>.Read(packer), Is.False,
                        "this fixture has no historical lifecycle hierarchy");
                }
                int count = 0;
                LastRegularRecordFullFlags.Clear();
                AddressedPredictionRecords.ReadSection((_, full, _, _) =>
                {
                    count++;
                    LastRegularRecordFullFlags.Add(full);
                }, packer);
                packer.SetBitPosition(end);
                return count;
            }
        }

        private static PredictionManager CreateManager(GameObject go)
        {
            var manager = go.AddComponent<PredictionManager>();
            Set(manager, "<tickRate>k__BackingField", 60);
            Set(manager, "<tickDelta>k__BackingField", 1f / 60);
            Set(manager, "<localTick>k__BackingField", 16UL);
            Set(manager, "<localTickInContext>k__BackingField", 16UL);
            Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
            Set(manager, "_updateViewMode", UpdateViewMode.None);
            return manager;
        }

        private static History<FULL_STATE<RecoveryProbeState>> Attach(
            RecoveryProbeIdentity identity, PredictionManager manager, PredictedComponentID id)
        {
            identity.Attach(manager, id);
            identity.fullPredictedState = State(900);
            var predicted = new History<FULL_STATE<RecoveryProbeState>>(200);
            predicted.Write(0, State(900));
            Set(typeof(PredictedIdentity<RecoveryProbeState>), identity, "_stateHistory", predicted);
            var verified = manager.GetVerifiedHistory<FULL_STATE<RecoveryProbeState>>(id, out _);
            verified.Write(8, State(80));
            verified.Write(10, State(100));
            Set(typeof(PredictedIdentity<RecoveryProbeState>), identity, "_verifiedHistory", verified);
            identity.lastVerifiedTick = 10;
            Get<List<PredictedIdentity>>(manager, "_systems").Add(identity);
            Set(manager, "_systemsCount", 1);
            Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Add(id, identity);
            return verified;
        }

        private static FULL_STATE<RecoveryProbeState> State(int value)
        {
            var state = new FULL_STATE<RecoveryProbeState> { state = new RecoveryProbeState { value = value } };
            state.prediction.wasOnSimulationStartCalled = true;
            return state;
        }

        private static T Get<T>(PredictionManager manager, string name) =>
            (T)Field(typeof(PredictionManager), name).GetValue(manager);
        private static void Set(PredictionManager manager, string name, object value) =>
            Set(typeof(PredictionManager), manager, name, value);
        private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
        private static FieldInfo Field(Type type, string name)
        {
            var field = type.GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return field;
        }
        private static object Invoke(PredictionManager manager, string name, params object[] args)
        {
            var method = typeof(PredictionManager).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            return method.Invoke(manager, args);
        }
    }

    public struct RecoveryProbeState : IPredictedData<RecoveryProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class RecoveryProbeIdentity : PredictedIdentity<RecoveryProbeState>
    {
        public Exception readFailure;
        public bool failFullState;
        public int deltaReads;
        public bool supplyInput;
        public int inputReads;
        public int lastInput;
        public bool recordCallbacks;
        public string callbackFailurePhase;
        public ulong callbackFailureTick;
        public bool callbackFailureAnyTick;
        public bool callbackFailureVerifiedOnly = true;
        public int callbackFailures;
        public readonly List<(ulong tick, string phase)> callbackObservations = new();
        private ulong _preparedCallbackTick = ulong.MaxValue;
        public override bool hasInput => supplyInput;
        internal override bool HasInputAt(ulong tick) => supplyInput;
        public override void WriteFirstInput(ulong localTick, BitPacker packer) => Packer<int>.Write(packer, 42);
        public override void ReadFirstInput(ulong localTick, BitPacker packer)
        {
            inputReads++;
            lastInput = Packer<int>.Read(packer);
        }
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
            myType = GetType();
        }
        public void ObserveCallback(string phase)
        {
            if (!recordCallbacks || !predictionManager.isSimulating)
                return;
            ulong tick = predictionManager.localTickInContext;
            if (phase == "prepare") _preparedCallbackTick = tick;
            if (phase == "latestUnity" && _preparedCallbackTick != tick)
                return;
            callbackObservations.Add((tick, phase));
            if (phase == callbackFailurePhase && (callbackFailureAnyTick || tick == callbackFailureTick) &&
                (!callbackFailureVerifiedOnly || predictionManager.isVerifiedAndReplaying))
            {
                callbackFailures++;
                throw new InvalidOperationException($"injected recovery callback failure [{phase}] at tick {tick}");
            }
        }
        public List<string> CallbacksAt(ulong tick)
        {
            var result = new List<string>();
            foreach (var observation in callbackObservations)
                if (observation.tick == tick) result.Add(observation.phase);
            return result;
        }
        internal override void OnPrepareSimulationInputs(ulong tick, float delta) => ObserveCallback("prepare");
        protected override void Simulate(ref RecoveryProbeState state, float delta)
        {
            ObserveCallback("simulate");
            state.value++;
        }
        protected override void LateSimulate(ref RecoveryProbeState state, float delta) => ObserveCallback("late");
        public override void PostSimulate() => ObserveCallback("post");
        protected override void GetUnityState(ref RecoveryProbeState state) => ObserveCallback("latestUnity");
        protected override void WriteDeltaState(BitPacker packer, in RecoveryProbeState baseline, in RecoveryProbeState current)
            => Packer<int>.Write(packer, current.value - baseline.value);
        protected override void ReadDeltaState(BitPacker packer, in RecoveryProbeState baseline, ref RecoveryProbeState state)
        {
            deltaReads++;
            if (readFailure != null) throw readFailure;
            state.value = baseline.value + Packer<int>.Read(packer);
        }
        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            if (failFullState && readFailure != null) throw readFailure;
            base.ReadFirstState(tick, packer, serverTick);
        }
    }
}
