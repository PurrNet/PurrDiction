using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

public static class ReliablePipelineSignals
{
    private static readonly ScenarioStateLedger<HashSet<PlayerID>> Finished = new();

    [ServerRpc(requireOwnership: false)]
    public static void ReportFinished(int scenarioIndex, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!Finished.TryGet(scenarioIndex, 0, out var players))
        {
            players = new HashSet<PlayerID>();
            Finished.TryAdd(scenarioIndex, 0, players);
        }
        players.Add(info.sender);
    }

    public static bool AllFinished(int scenarioIndex, int count)
        => Finished.TryGet(scenarioIndex, 0, out var players) && players.Count == count;
}

/// <summary>Opt-in tick-by-tick fresh snapshot cadence evidence on the actual transport.</summary>
public sealed class ReliablePipelineScenario : Scenario
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const float Timeout = 30;
    private const double SettleSeconds = 3;
    private const double TailSeconds = 6;
    private const double BlackoutSeconds = 0.35;
    private const int ReadyBarrier = 45001;
    private const int DoneBarrier = 45002;

    [Serializable]
    public sealed class Sample
    {
        public double elapsed;
        public ulong tick;
        public string player;
        public string phase;
        public ulong ack;
        public ulong verified;
        public ulong sent;
        public ulong preparedFrame;
        public ulong preparedBaseline;
        public ulong lastSent;
        public bool awaitingFull;
        public ulong lastFull;
        public ulong received;
        public ulong fullReceived;
    }

    [Serializable]
    public sealed class Arrival
    {
        public double elapsed;
        public ulong tick;
        public ulong baseline;
        public ulong previousVerified;
        public bool full;
    }

    [Serializable]
    public sealed class ReportData
    {
        public string fixtureVersion = "ordered-frame-pipeline-v1";
        public string role;
        public string player;
        public string fault;
        public int tickRate;
        public bool success;
        public string failure;
        public bool explicitBaselineRecovery;
        public bool blackoutApplied;
        public bool blackoutRestored;
        public bool blackoutTailQuiet;
        public bool mainThreadStalled;
        public double mainThreadStallSeconds;
        public double faultAt;
        public double faultRestoredAt;
        public double tailStartedAt;
        public double tailEndedAt;
        public int samplesOmitted;
        public int arrivalsOmitted;
        public readonly List<Sample> samples = new();
        public readonly List<Arrival> arrivals = new();
    }

    private ScenarioContext _ctx;
    private ReportData _report;
    private double _start;
    private string _phase = "setup";
    private bool _recording;
    private ulong _sampleTick;
    private string _sampleError;
    private BaselineRecoveryScenario _recovery;
#if DEBUG || SIMULATE_NETWORK
    private UDPTransport _udp;
    private NetworkSimulation _originalSimulation;
    private bool _blackout;
#endif

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        CommandLineUtils.TryGetArgument("-pipelineFault", out string fault);
        if (!string.IsNullOrEmpty(fault) && fault != "recovery") return;
        _recovery = gameObject.AddComponent<BaselineRecoveryScenario>();
        _recovery.Setup(ctx, manager);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        _ctx = ctx;
        _start = Time.realtimeSinceStartupAsDouble;
        CommandLineUtils.TryGetArgument("-pipelineFault", out string fault);
        _report = new ReportData { role = ctx.role.ToString(), player = ctx.networkManager.isLocalPlayerReady ?
            ctx.networkManager.localPlayer.ToString() : "server", tickRate = ctx.predictionManager.tickRate,
            fault = string.IsNullOrEmpty(fault) ? "recovery" : fault };
        ctx.networkManager.tickModule.onPostTick += SampleTick;
        ctx.predictionManager.onStartingToRollback += CaptureArrivals;
        ScenarioResult result;
        try
        {
            if (_report.fault != "recovery" && _report.fault != "blackout" && _report.fault != "control" && _report.fault != "stall")
                throw new InvalidOperationException("-pipelineFault must be recovery, blackout, stall or control.");
            await Delay(3);
            await ScenarioBarrier.Wait(ctx, ReadyBarrier, Timeout);
            _phase = "transient";
            _recording = true;
            _report.faultAt = Elapsed;
            if (_report.fault == "recovery")
            {
                var recovered = await _recovery.RunScenario(ctx);
                if (!recovered.success) throw new InvalidOperationException("Explicit baseline recovery failed: " + recovered.message);
                _report.explicitBaselineRecovery = true;
            }
            if (!ctx.isServer && _report.fault == "stall")
            {
                // A bounded actual main-thread interruption, including application/transport polling.
                // A requested sleep may finish slightly early on Windows. Enforce the
                // measured duration rather than weakening the fault's validation threshold.
                var stall = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    double remaining = 0.35 - stall.Elapsed.TotalSeconds;
                    System.Threading.Thread.Sleep(Math.Max(1, (int)Math.Ceiling(remaining * 1000)));
                } while (stall.Elapsed.TotalSeconds < 0.35);
                _report.mainThreadStallSeconds = stall.Elapsed.TotalSeconds;
                _report.mainThreadStalled = true;
            }
            if (!ctx.isServer && _report.fault == "blackout")
            {
                StartBlackout();
                await Delay(0.2);
                ulong tailFrames = ctx.predictionManager.framesReceivedTotal;
                await Delay(BlackoutSeconds - 0.2);
                _report.blackoutTailQuiet = ctx.predictionManager.framesReceivedTotal == tailFrames;
                RestoreBlackout();
                if (!_report.blackoutTailQuiet) throw new InvalidOperationException("Enforced blackout did not have a quiet receive tail.");
            }
            _report.faultRestoredAt = Elapsed;
            await Delay(SettleSeconds);
            _phase = "tail";
            _report.tailStartedAt = Elapsed;
            await Delay(TailSeconds);
            _report.tailEndedAt = Elapsed;
            _phase = "complete";
            if (!ctx.isServer) ReliablePipelineSignals.ReportFinished(ctx.scenarioIndex);
            else await Wait(() => ReliablePipelineSignals.AllFinished(ctx.scenarioIndex, ctx.externalClientCount), "client completion reports missing");
            _recording = false;
            ValidateLocal();
            await ScenarioBarrier.Wait(ctx, DoneBarrier, Timeout);
            result = ScenarioResult.Ok("Per-tick fresh send cadence and client verified progress recorded and validated.");
        }
        catch (Exception exception) { result = ScenarioResult.Fail(exception.Message); }
        finally
        {
            _recording = false;
            RestoreBlackout();
            ctx.networkManager.tickModule.onPostTick -= SampleTick;
            ctx.predictionManager.onStartingToRollback -= CaptureArrivals;
        }
        _report.success = result.success;
        _report.failure = result.success ? null : result.message;
        if (CommandLineUtils.TryGetArgument("-results", out string path))
            File.WriteAllText(Path.ChangeExtension(path, "reliable-pipeline.json"), JsonConvert.SerializeObject(_report, Formatting.Indented));
        return result;
    }

    private void SampleTick()
    {
        if (!_recording || _sampleError != null) return;
        try
        {
            var world = _ctx.predictionManager;
            if (_sampleTick == world.localTick) return;
            _sampleTick = world.localTick;
            if (_ctx.isServer)
            {
                foreach (object frame in (IList)Get(world, "_clientFrames"))
                {
                    var player = (PlayerID)Get(frame, "player");
                    Add(new Sample { elapsed = Elapsed, tick = world.localTick, phase = _phase, player = player.ToString(),
                        ack = ReadAck(player), sent = U64(frame, "sentVisibilityTick"),
                        preparedFrame = U64(frame, "preparedFrameTick"),
                        preparedBaseline = U64(frame, "preparedBaselineTick"),
                        lastSent = U64(frame, "lastSentFrameTick"),
                        lastFull = U64(frame, "lastFullFrameSentTick") });
                }
            }
            else Add(new Sample { elapsed = Elapsed, tick = world.localTick, phase = _phase, player = _report.player,
                ack = U64(world, "_ackedServerTick"), verified = U64(world, "_verifiedServerTick"),
                awaitingFull = (bool)Get(world, "_awaitingFullFrame"),
                received = world.framesReceivedTotal, fullReceived = world.fullFramesReceivedTotal });
        }
        catch (Exception exception) { _sampleError = exception.Message; }
    }

    private void CaptureArrivals()
    {
        if (!_recording || _ctx.isServer || _sampleError != null) return;
        try
        {
            var world = _ctx.predictionManager;
            foreach (object frame in (IEnumerable)Get(world, "_deltas"))
            {
                if (_report.arrivals.Count >= 4096) { _report.arrivalsOmitted++; continue; }
                _report.arrivals.Add(new Arrival { elapsed = Elapsed, tick = U64(frame, "serverTick"),
                    baseline = U64(frame, "baselineTick"), full = (bool)Get(frame, "fullFrame"),
                    previousVerified = U64(world, "_verifiedServerTick") });
            }
        }
        catch (Exception exception) { _sampleError = exception.Message; }
    }

    private void Add(Sample sample)
    {
        if (_report.samples.Count < 12000) _report.samples.Add(sample); else _report.samplesOmitted++;
    }

    private void ValidateLocal()
    {
        if (_sampleError != null) throw new InvalidOperationException("Sampling failed: " + _sampleError);
        if (_report.samplesOmitted != 0 || _report.arrivalsOmitted != 0) throw new InvalidOperationException("Evidence cap reached.");
        var players = new HashSet<string>();
        foreach (var sample in _report.samples) if (sample.phase == "tail") players.Add(sample.player);
        if (players.Count != (_ctx.isServer ? _ctx.externalClientCount : 1)) throw new InvalidOperationException("Tail peer sample inventory differs.");
        foreach (string player in players)
        {
            var samples = _report.samples.FindAll(s => s.player == player && s.phase == "tail");
            if (samples.Count < _report.tickRate * 5) throw new InvalidOperationException($"Insufficient maintained tick rate for {player}.");
            var first = samples[0];
            var last = samples[samples.Count - 1];
            int fresh = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                var previous = samples[i - 1];
                var current = samples[i];
                if (current.tick != previous.tick + 1) throw new InvalidOperationException($"Tick sample gap for {player}.");
                if (current.ack < previous.ack || current.verified < previous.verified) throw new InvalidOperationException($"ACK/verified regression for {player}.");
                if (_ctx.isServer && current.lastSent < previous.lastSent) throw new InvalidOperationException($"Sent frame regression for {player}.");
                if (current.sent > previous.sent) fresh++;
            }
            if (last.ack - first.ack < 0.85 * (last.tick - first.tick)) throw new InvalidOperationException($"ACK cadence did not recover for {player}.");
            if (_ctx.isServer)
            {
                // A server update rate below the tick rate coalesces ticks by design.
                int updateRate = _ctx.predictionManager.serverUpdateRate;
                double expectedShare = updateRate > 0 && updateRate < _report.tickRate ? (double)updateRate / _report.tickRate : 1.0;
                if (fresh < 0.9 * expectedShare * (samples.Count - 1)) throw new InvalidOperationException($"Fresh snapshot cadence {fresh}/{samples.Count - 1} for {player} (update rate {updateRate}, tick rate {_report.tickRate}).");
                if (last.lastFull != first.lastFull) throw new InvalidOperationException($"Full snapshot fallback during healthy tail for {player}.");
                // Deltas follow a full on the ordered stream without waiting for its acknowledgement.
                if (_report.fault == "recovery" && !_report.samples.Exists(s => s.player == player && s.lastFull > s.ack &&
                    s.lastSent > s.lastFull)) throw new InvalidOperationException($"No delta sent past an unacknowledged full frame for {player}.");
            }
            else
            {
                if (last.fullReceived != first.fullReceived) throw new InvalidOperationException("Full snapshot received during healthy tail.");
                if (samples.Exists(s => s.awaitingFull)) throw new InvalidOperationException("Client waited for a full frame during the healthy tail.");
            }
        }
    }

    private void StartBlackout()
    {
#if DEBUG || SIMULATE_NETWORK
        _udp = _ctx.networkManager.transport as UDPTransport;
        if (!_udp) throw new InvalidOperationException("UDP simulation required.");
        _originalSimulation = _udp.networkSimulation;
        var settings = _originalSimulation;
        settings.includeInBuild = true;
        settings.simulatePacketLoss = true;
        settings.packetLossChance = 100;
        _udp.networkSimulation = settings;
        _blackout = true;
        object client = Get(_udp, "_client");
        _report.blackoutApplied = (bool)Get(client, "SimulatePacketLoss") && Convert.ToInt32(Get(client, "SimulationPacketLossChance")) == 100;
        if (!_report.blackoutApplied) throw new InvalidOperationException("Underlying UDP blackout settings differ.");
#else
        throw new InvalidOperationException("Build cannot simulate a receive blackout.");
#endif
    }

    private void RestoreBlackout()
    {
#if DEBUG || SIMULATE_NETWORK
        if (!_blackout) return;
        if (_udp) _udp.networkSimulation = _originalSimulation;
        _blackout = false;
        _report.blackoutRestored = true;
#endif
    }

    private async UniTask Delay(double seconds)
    {
        double until = Elapsed + seconds;
        while (Elapsed < until)
        {
            if (_sampleError != null) throw new InvalidOperationException(_sampleError);
            await UniTask.NextFrame(_ctx.cancellationToken);
        }
    }

    private async UniTask Wait(Func<bool> predicate, string message)
    {
        double deadline = Elapsed + Timeout;
        while (!predicate())
        {
            if (Elapsed >= deadline) throw new TimeoutException(message);
            await UniTask.NextFrame(_ctx.cancellationToken);
        }
    }

    private ulong ReadAck(PlayerID player)
    {
        var ticks = (IDictionary)Get(_ctx.predictionManager, "_clientTicks");
        return ticks.Contains(player) ? U64(ticks[player], "ackedServerTick") : 0;
    }
    private double Elapsed => Time.realtimeSinceStartupAsDouble - _start;
    private static ulong U64(object target, string name) => Convert.ToUInt64(Get(target, name));
    private static object Get(object target, string name) => target.GetType().GetField(name, Fields)?.GetValue(target) ??
        target.GetType().GetProperty(name, Fields)?.GetValue(target) ?? throw new MissingMemberException(target.GetType().FullName, name);
}
