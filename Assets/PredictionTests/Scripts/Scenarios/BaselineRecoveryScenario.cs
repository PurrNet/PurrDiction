using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public static class BaselineRecoverySignals
{
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();
    private static readonly ScenarioStateLedger<ulong> CreationTicks = new();

    [ObserversRpc(runLocally: true)]
    public static void SetProbeCreatedTick(int scenarioIndex, ulong tick)
        => CreationTicks.TryAdd(scenarioIndex, 0, tick);

    public static bool TryGetCreationTick(int index, out ulong tick)
        => CreationTicks.TryGet(index, 0, out tick);

    [ServerRpc(requireOwnership: false)]
    public static void Report(int scenarioIndex, string json, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!Reports.TryGet(scenarioIndex, 0, out var reports))
        {
            reports = new Dictionary<PlayerID, string>();
            Reports.TryAdd(scenarioIndex, 0, reports);
        }
        reports[info.sender] = json;
    }

    public static bool TryGet(int index, out Dictionary<PlayerID, string> reports)
        => Reports.TryGet(index, 0, out reports);
}

/// <summary>Opt-in, real-wire baseline loss and recovery; never selected by the ordinary suite.</summary>
public sealed class BaselineRecoveryScenario : Scenario
{
    private const float Timeout = 25f;
    private const double BlackoutSeconds = 1.25;
    private const int ReadyBarrier = 44001;
    private const int DoneBarrier = 44002;
    private const int DigestChannel = 44003;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static BaselineRecoveryScenario _activeFault;

    [Serializable]
    public sealed class Trace
    {
        public double elapsedSeconds;
        public string utc;
        public string text;
    }

    [Serializable]
    public sealed class Sample
    {
        public double elapsedSeconds;
        public ulong localTick;
        public string player;
        public ulong ack;
        public ulong verified;
        public ulong lastSent;
        public ulong lastFullSent;
        public ulong sentVisibility;
        public bool pending;
        public ulong requiredAfter;
        public ulong fullFrames;
        public ulong deltaFrames;
    }

    [Serializable]
    public sealed class ReportData
    {
        public string fixtureVersion = "baseline-recovery-ordered-v1";
        public string role;
        public string player;
        public string fault;
        public int tickRate;
        public bool success;
        public string failure;
        public bool injectionArmed;
        public bool faultInjected;
        public int historyCountBefore;
        public int historyCountAfter;
        public string identity;
        public ulong ackBefore;
        public ulong verifiedBefore;
        public int expectedBaselineErrors;
        public int requestTraceCount;
        public int serveTraceCount;
        public int coveredTraceCount;
        public ulong probeCreatedTick;
        public ulong injectedFrameTick;
        public ulong injectedBaselineTick;
        public ulong injectedActualGap;
        public int queuedFramesAtInjection;
        public int skippedIneligibleBatches;
        public bool serverAcksCoveredProbe;
        public bool pendingObserved;
        public bool pendingCleared;
        public bool historyRestored;
        public bool fullReceived;
        public ulong fullFramesBefore;
        public ulong fullFramesAfter;
        public ulong recoveredAck;
        public ulong recoveredVerified;
        public double faultAtSeconds;
        public string faultAtUtc;
        public double firstRequestAtSeconds = -1;
        public double recoveryAtSeconds = -1;
        public double recoveryMilliseconds;
        public bool blackoutEnabled;
        public bool blackoutRestored;
        public bool blackoutSettingsApplied;
        public bool blackoutTailHadNoFrames;
        public double blackoutMilliseconds;
        public ulong framesDuringBlackout;
        public bool resumedVerifiedProgress;
        public bool freshDeltasResumed;
        public int tracesOmitted;
        public int samplesOmitted;
        public readonly List<Trace> traces = new();
        public readonly List<Sample> samples = new();
        public readonly List<ReportData> clientReports = new();
    }

    private GameObject _prefab;
    private int _prefabId;
    private ScenarioContext _ctx;
    private ReportData _report;
    private BaselineRecoveryProbe _probe;
    private object _history;
    private double _startedAt;
    private double _nextSampleAt;
    private string _injectionError;
    private bool _armed;
    private bool _blackoutActive;
    private ulong _framesBeforeBlackout;
    private ulong _blackoutTailFrames;
    private bool _blackoutTailSampled;
#if DEBUG || SIMULATE_NETWORK
    private UDPTransport _udp;
    private NetworkSimulation _originalSimulation;
#endif

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<BaselineRecoveryProbe>("BaselineRecoveryProbe");
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public static bool TryConsumeExpectedBaselineError(string text, LogType type)
    {
        var active = _activeFault;
        if (!active || active._report == null || !active._report.faultInjected || type != LogType.Error)
            return false;
        string id = active._report.identity;
        // Only this deliberately invalidated identity and this exact baseline failure category.
        if (string.IsNullOrEmpty(id) || !text.Contains($"Discarded prediction record {id} (BaselineRecoveryProbe);") ||
            !text.Contains("Missing acknowledged state baseline") || !text.Contains(id))
            return false;
        active._report.expectedBaselineErrors++;
        active.Record("[BaselineRecoveryFixture] expected missing-baseline error: " + text);
        return true;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        _ctx = ctx;
        _startedAt = Time.realtimeSinceStartupAsDouble;
        CommandLineUtils.TryGetArgument("-brFault", out string fault);
        fault = string.IsNullOrEmpty(fault) ? "missing" : fault;
        _report = new ReportData { role = ctx.role.ToString(), player = ctx.networkManager.isLocalPlayerReady ? ctx.networkManager.localPlayer.ToString() : "server",
            tickRate = ctx.predictionManager.tickRate, fault = fault };
        Application.logMessageReceived += OnLog;
        ctx.predictionManager.onStartingToRollback += InjectBeforeRealBatch;
        ctx.predictionManager.onRollbackFinished += ObserveAfterBatch;
        ScenarioResult result;
        try
        {
            if (fault != "missing" && fault != "blackout")
                throw new InvalidOperationException("-brFault must be missing or blackout.");
            if (!CommandLineUtils.HasFlag("-physicsEventTrace"))
                throw new InvalidOperationException("This scenario requires -physicsEventTrace to prove the actual recovery RPC path.");
            result = await Run(ctx);
            _report.success = result.success;
            _report.failure = result.success ? null : result.message;
        }
        catch (Exception exception)
        {
            _report.success = false;
            _report.failure = exception.Message;
            result = ScenarioResult.Fail(exception.Message);
        }
        finally
        {
            _armed = false;
            _activeFault = null;
            RestoreBlackout();
            ctx.predictionManager.onStartingToRollback -= InjectBeforeRealBatch;
            ctx.predictionManager.onRollbackFinished -= ObserveAfterBatch;
            Application.logMessageReceived -= OnLog;
            SaveReport();
        }
        return result;
    }

    private async UniTask<ScenarioResult> Run(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        world.TryGetPrefab(_prefab, out _prefabId);
        if (ctx.isServer && !world.hierarchy.Create(_prefab, Vector3.zero, Quaternion.identity).HasValue)
            return ScenarioResult.Fail("Recovery probe creation failed.");
        if (ctx.isServer)
            BaselineRecoverySignals.SetProbeCreatedTick(ctx.scenarioIndex, world.localTick);
        await Wait(() => BaselineRecoverySignals.TryGetCreationTick(ctx.scenarioIndex, out _report.probeCreatedTick),
            "authoritative probe creation tick was not received");
        await Wait(() => (_probe = FindProbe()) != null, "recovery probe did not appear");
        await UniTask.WaitForSeconds(2f, cancellationToken: ctx.cancellationToken);
        await ScenarioBarrier.Wait(ctx, ReadyBarrier, Timeout);
        if (ctx.isServer)
        {
            await Wait(() => ServerSnapshots().Count == ctx.externalClientCount &&
                ServerSnapshots().TrueForAll(s => s.ack > _report.probeCreatedTick + 2),
                "server ACKs did not cover the probe creation");
            _report.serverAcksCoveredProbe = true;
            await Wait(() => BaselineRecoverySignals.TryGet(ctx.scenarioIndex, out var reports) && reports.Count == ctx.externalClientCount,
                "client recovery reports did not arrive");
            BaselineRecoverySignals.TryGet(ctx.scenarioIndex, out var clients);
            foreach (var pair in clients)
            {
                var client = JsonConvert.DeserializeObject<ReportData>(pair.Value);
                _report.clientReports.Add(client);
                if (!client.success) return ScenarioResult.Fail($"Client {pair.Key} did not recover: {client.failure}");
                await Wait(() => ReadServerAck(pair.Key) >= client.recoveredAck + (ulong)world.tickRate,
                    $"Server ACK did not advance a full tick-rate interval beyond recovery for {pair.Key}.");
                // The periodic sampler may stop immediately before this boundary.
                // Keep the exact observed terminal condition as independent evidence.
                AddSample(ServerSnapshots().Find(s => s.player == pair.Key.ToString()) ??
                    throw new InvalidOperationException($"Server snapshot missing for {pair.Key}."));
            }
            int repairTraces = _report.serveTraceCount + (_report.fault == "blackout" ? _report.coveredTraceCount : 0);
            if (repairTraces < ctx.externalClientCount)
                return ScenarioResult.Fail($"Only {repairTraces}/{ctx.externalClientCount} server repair-decision traces observed.");
            // Prove new per-player sends after acknowledged full-frame recovery.
            var previous = ServerSnapshots();
            ulong deltasBefore = world.deltaFramesWrittenTotal;
            await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);
            foreach (var pair in clients)
            {
                var prior = previous.Find(s => s.player == pair.Key.ToString());
                var now = ServerSnapshots().Find(s => s.player == pair.Key.ToString());
                if (prior == null || now == null || now.ack <= prior.ack || now.sentVisibility <= prior.sentVisibility ||
                    now.lastFullSent != prior.lastFullSent)
                    return ScenarioResult.Fail($"No fresh acknowledged per-player frames after recovery for {pair.Key}.");
            }
            if (world.deltaFramesWrittenTotal <= deltasBefore)
                return ScenarioResult.Fail("No new delta frames were written after full-frame recovery.");
            _report.freshDeltasResumed = true;
        }
        else
        {
            var historyField = typeof(PredictedIdentity<BaselineRecoveryProbe.State>).GetField("_verifiedHistory", Fields);
            _history = historyField?.GetValue(_probe) ?? throw new InvalidOperationException("Probe verified history is unavailable.");
            await Wait(() => !(bool)Get(world, "_historyResyncPending"), "pre-existing recovery never settled before fault injection");
            if (HistoryCount() == 0 || U64(world, "_ackedServerTick") == 0)
                return ScenarioResult.Fail("No established nonzero client baseline before injection.");
            await UniTask.NextFrame(ctx.cancellationToken);
            _report.injectionArmed = _armed = true;
            await Wait(() => _report.faultInjected || _injectionError != null, "no real received batch available for injection");
            if (_injectionError != null) return ScenarioResult.Fail(_injectionError);
            await Wait(() => _report.pendingCleared && _report.historyRestored && _report.fullReceived && !_blackoutActive,
                "missing baseline did not recover through an applied full frame");
            if (_report.expectedBaselineErrors == 0 || _report.requestTraceCount == 0 || !_report.pendingObserved)
                return ScenarioResult.Fail("The actual missing-baseline error, request RPC or pending transition was not observed.");
            // Deltas wait for a full frame after the fault, so only a covering full can move these past it.
            if (_report.recoveredAck < _report.injectedFrameTick || _report.recoveredVerified < _report.injectedFrameTick)
                return ScenarioResult.Fail("Recovery did not apply a covering full frame before advancing its ACK.");
            if (_report.fault == "blackout" && (!_report.blackoutEnabled || !_report.blackoutRestored ||
                !_report.blackoutSettingsApplied || !_report.blackoutTailHadNoFrames ||
                _report.firstRequestAtSeconds < _report.faultAtSeconds ||
                _report.firstRequestAtSeconds > _report.faultAtSeconds + _report.blackoutMilliseconds / 1000d))
                return ScenarioResult.Fail("The real recovery request was not observed inside the enforced transport blackout.");
            ulong recovered = _report.recoveredAck;
            await Wait(() => U64(world, "_ackedServerTick") >= recovered + (ulong)world.tickRate,
                "verified ACK progress did not resume after the full frame");
            AddSample(ClientSnapshot());
            _report.resumedVerifiedProgress = true;
            if (_probe.currentState.sentinel != BaselineRecoveryProbe.ExpectedSentinel)
                return ScenarioResult.Fail("Probe contents did not recover.");
            _report.success = true;
            // Keep cross-process proof summaries small; detailed traces/samples stay in the local artifact.
            BaselineRecoverySignals.Report(ctx.scenarioIndex, JsonConvert.SerializeObject(new
            {
                _report.fixtureVersion, _report.role, _report.player, _report.fault, _report.success,
                _report.recoveredAck, _report.recoveredVerified, _report.recoveryMilliseconds,
                _report.expectedBaselineErrors, _report.requestTraceCount, _report.pendingObserved,
                _report.pendingCleared, _report.historyRestored, _report.fullReceived,
                _report.resumedVerifiedProgress, _report.blackoutEnabled, _report.blackoutRestored
            }));
        }
        await ScenarioBarrier.Wait(ctx, DoneBarrier, Timeout);
        return await DigestExchange.Compare(ctx, DigestChannel, $"sentinel={_probe.currentState.sentinel}", Timeout);
    }

    private void InjectBeforeRealBatch()
    {
        if (!_armed || _ctx.isServer) return;
        // This hook also runs for stale/duplicate batches, full resets and oversized gaps.
        // Invalidate only for the first frame that will actually attempt a delta baseline read.
        var world = _ctx.predictionManager;
        if ((bool)Get(world, "_historyResyncPending")) { _report.skippedIneligibleBatches++; return; }
        ulong verified = U64(world, "_verifiedServerTick");
        object eligible = null;
        var queued = (IEnumerable)Get(world, "_deltas");
        int queuedCount = 0;
        foreach (object frame in queued)
        {
            queuedCount++;
            if (eligible != null) continue;
            ulong tick = U64(frame, "serverTick");
            if (tick <= verified) continue;
            ulong baseline = U64(frame, "baselineTick");
            if ((bool)Get(frame, "fullFrame") || tick - verified > 32 ||
                baseline <= _report.probeCreatedTick + 2)
            {
                _report.skippedIneligibleBatches++;
                return;
            }
            eligible = frame;
        }
        if (eligible == null) { _report.skippedIneligibleBatches++; return; }
        _report.injectedFrameTick = U64(eligible, "serverTick");
        _report.injectedBaselineTick = U64(eligible, "baselineTick");
        _report.injectedActualGap = _report.injectedFrameTick - verified;
        _report.queuedFramesAtInjection = queuedCount;
        _armed = false;
        try
        {
            _report.historyCountBefore = HistoryCount();
            if (_report.historyCountBefore == 0) throw new InvalidOperationException("Baseline history was empty before injection.");
            _report.identity = _probe.id.ToString();
            _report.ackBefore = U64(world, "_ackedServerTick");
            _report.verifiedBefore = U64(world, "_verifiedServerTick");
            _report.fullFramesBefore = world.fullFramesReceivedTotal;
            _report.faultAtSeconds = Elapsed;
            _report.faultAtUtc = DateTime.UtcNow.ToString("O");
            if (_report.fault == "blackout") StartBlackout();
            _activeFault = this;
            _report.faultInjected = true;
            _history.GetType().GetMethod("Clear", Fields).Invoke(_history, null);
            _report.historyCountAfter = HistoryCount();
            if (_report.historyCountAfter != 0) throw new InvalidOperationException("History invalidation did not empty the baseline.");
            Record($"[BaselineRecoveryFixture] injected identity={_report.identity} ack={_report.ackBefore} historyBefore={_report.historyCountBefore} historyAfter=0 frame={_report.injectedFrameTick} baseline={_report.injectedBaselineTick} gap={_report.injectedActualGap} probeCreated={_report.probeCreatedTick} queued={queuedCount}");
        }
        catch (Exception exception) { _injectionError = exception.Message; }
    }

    private void ObserveAfterBatch()
    {
        if (_report == null || !_report.faultInjected) return;
        var world = _ctx.predictionManager;
        bool pending = (bool)Get(world, "_historyResyncPending");
        _report.pendingObserved |= pending;
        _report.fullFramesAfter = world.fullFramesReceivedTotal;
        _report.fullReceived = _report.fullFramesAfter > _report.fullFramesBefore;
        _report.historyRestored = HistoryCount() > 0 && _probe.currentState.sentinel == BaselineRecoveryProbe.ExpectedSentinel;
        if (_report.pendingObserved && !pending && _report.fullReceived && _report.historyRestored)
        {
            _report.pendingCleared = true;
            if (_report.recoveryAtSeconds < 0)
            {
                _report.recoveryAtSeconds = Elapsed;
                _report.recoveryMilliseconds = 1000d * (_report.recoveryAtSeconds - _report.faultAtSeconds);
                _report.recoveredAck = U64(world, "_ackedServerTick");
                _report.recoveredVerified = U64(world, "_verifiedServerTick");
            }
            _activeFault = null;
        }
    }

    private void OnLog(string text, string stack, LogType type)
    {
        if (!text.StartsWith("[HistoryResyncTrace]", StringComparison.Ordinal) &&
            !text.StartsWith("[PhysicsEventTrace] full", StringComparison.Ordinal)) return;
        Record(text);
        if (_report.faultInjected && text.StartsWith("[HistoryResyncTrace] resyncRequest ", StringComparison.Ordinal))
        {
            _report.requestTraceCount++;
            _report.pendingObserved |= (bool)Get(_ctx.predictionManager, "_historyResyncPending");
            if (_report.firstRequestAtSeconds < 0) _report.firstRequestAtSeconds = Elapsed;
        }
        if (text.StartsWith("[HistoryResyncTrace] resyncServe ", StringComparison.Ordinal))
        {
            _report.serveTraceCount++;
        }
        if (text.StartsWith("[HistoryResyncTrace] resyncCovered ", StringComparison.Ordinal)) _report.coveredTraceCount++;
    }

    private void Record(string text)
    {
        if (_report.traces.Count >= 256) { _report.tracesOmitted++; return; }
        _report.traces.Add(new Trace { elapsedSeconds = Elapsed, utc = DateTime.UtcNow.ToString("O"), text = text });
    }

    private async UniTask Wait(Func<bool> predicate, string failure)
    {
        double deadline = Elapsed + Timeout;
        while (!predicate())
        {
            _ctx.cancellationToken.ThrowIfCancellationRequested();
            if (_injectionError != null) throw new InvalidOperationException(_injectionError);
            if (Elapsed > deadline) throw new TimeoutException(failure);
            MaintainAndSample();
            await UniTask.NextFrame(_ctx.cancellationToken);
        }
        MaintainAndSample();
    }

    private void MaintainAndSample()
    {
        if (_blackoutActive && !_blackoutTailSampled && Elapsed - _report.faultAtSeconds >= 0.6)
        {
            _blackoutTailSampled = true;
            _blackoutTailFrames = _ctx.predictionManager.framesReceivedTotal;
        }
        if (_blackoutActive && Elapsed - _report.faultAtSeconds >= BlackoutSeconds) RestoreBlackout();
        if (Elapsed < _nextSampleAt) return;
        _nextSampleAt = Elapsed + 0.05;
        if (_ctx.isServer)
        {
            foreach (var sample in ServerSnapshots()) AddSample(sample);
        }
        else
        {
            AddSample(ClientSnapshot());
        }
    }

    private Sample ClientSnapshot()
    {
        var world = _ctx.predictionManager;
        return new Sample { elapsedSeconds = Elapsed, player = _report.player, localTick = world.localTick,
            ack = U64(world, "_ackedServerTick"), verified = U64(world, "_verifiedServerTick"),
            pending = (bool)Get(world, "_historyResyncPending"), requiredAfter = U64(world, "_historyResyncRequiredAfterTick"),
            fullFrames = world.fullFramesReceivedTotal, deltaFrames = world.framesReceivedTotal - world.fullFramesReceivedTotal };
    }

    private void AddSample(Sample sample)
    {
        if (_report.samples.Count < 1024) _report.samples.Add(sample); else _report.samplesOmitted++;
    }

    private List<Sample> ServerSnapshots()
    {
        var result = new List<Sample>();
        var world = _ctx.predictionManager;
        foreach (object frame in (IList)Get(world, "_clientFrames"))
        {
            var player = (PlayerID)Get(frame, "player");
            result.Add(new Sample { elapsedSeconds = Elapsed, player = player.ToString(), localTick = world.localTick,
                ack = ReadServerAck(player), lastSent = U64(frame, "lastSentFrameTick"),
                lastFullSent = U64(frame, "lastFullFrameSentTick"),
                sentVisibility = U64(frame, "sentVisibilityTick"), fullFrames = world.fullFramesSentTotal, deltaFrames = world.deltaFramesWrittenTotal });
        }
        return result;
    }

    private ulong ReadServerAck(PlayerID player)
    {
        var ticks = (IDictionary)Get(_ctx.predictionManager, "_clientTicks");
        return ticks.Contains(player) ? U64(ticks[player], "ackedServerTick") : 0;
    }

    private void StartBlackout()
    {
#if DEBUG || SIMULATE_NETWORK
        _udp = _ctx.networkManager.transport as UDPTransport;
        if (!_udp) throw new InvalidOperationException("UDP network simulation is required for the blackout control.");
        _originalSimulation = _udp.networkSimulation;
        var blackout = _originalSimulation;
        blackout.includeInBuild = true;
        blackout.simulatePacketLoss = true;
        blackout.packetLossChance = 100;
        _framesBeforeBlackout = _ctx.predictionManager.framesReceivedTotal;
        _udp.networkSimulation = blackout;
        _report.blackoutEnabled = _blackoutActive = true;
        object client = Get(_udp, "_client");
        _report.blackoutSettingsApplied = (bool)Get(client, "SimulatePacketLoss") &&
            Convert.ToInt32(Get(client, "SimulationPacketLossChance")) == 100;
        if (!_report.blackoutSettingsApplied) throw new InvalidOperationException("The underlying UDP loss settings were not applied.");
        Record("[BaselineRecoveryFixture] transport blackout enabled at the queued failing batch");
#else
        throw new InvalidOperationException("This build cannot enforce a transport blackout.");
#endif
    }

    private void RestoreBlackout()
    {
        if (!_blackoutActive) return;
#if DEBUG || SIMULATE_NETWORK
        if (_udp) _udp.networkSimulation = _originalSimulation;
#endif
        _blackoutActive = false;
        _report.blackoutRestored = true;
        _report.blackoutMilliseconds = 1000d * (Elapsed - _report.faultAtSeconds);
        _report.framesDuringBlackout = _ctx.predictionManager.framesReceivedTotal - _framesBeforeBlackout;
        _report.blackoutTailHadNoFrames = _blackoutTailSampled && _ctx.predictionManager.framesReceivedTotal == _blackoutTailFrames;
        Record("[BaselineRecoveryFixture] transport blackout restored");
    }

    private BaselineRecoveryProbe FindProbe()
    {
        var world = _ctx.predictionManager;
        foreach (var entry in world.hierarchy.currentState.spawnedPrefabs)
            if (entry.prefabId == _prefabId && entry.instanceId.TryGetComponent<BaselineRecoveryProbe>(world, out var probe)) return probe;
        return null;
    }

    private int HistoryCount() => (int)_history.GetType().GetProperty("Count", Fields).GetValue(_history);
    private double Elapsed => Time.realtimeSinceStartupAsDouble - _startedAt;
    private static ulong U64(object target, string name) => Convert.ToUInt64(Get(target, name));
    private static object Get(object target, string name)
        => target.GetType().GetField(name, Fields)?.GetValue(target) ??
           target.GetType().GetProperty(name, Fields)?.GetValue(target) ??
           throw new MissingMemberException(target.GetType().FullName, name);

    private void SaveReport()
    {
        if (!CommandLineUtils.TryGetArgument("-results", out string results)) return;
        File.WriteAllText(Path.ChangeExtension(results, "baseline-recovery.json"), JsonConvert.SerializeObject(_report, Formatting.Indented));
    }
}
