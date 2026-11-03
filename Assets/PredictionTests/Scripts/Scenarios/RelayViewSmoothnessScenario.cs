using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Per-frame view samples of the relay smoothness probes. Each probe's state is a stamp that
/// advances by exactly one per server tick, so the rate its view advances is its on-screen speed.
/// </summary>
public static class RelayViewLog
{
    public const int Control = 0;
    public const int RelayStateful = 1;
    public const int RelayDeterministic = 2;
    public const int RelayModule = 3;
    public const int KindCount = 4;

    public static readonly string[] names = { "control", "relayStateful", "relayDeterministic", "relayModule" };

    public struct Sample
    {
        public int frame;
        public int kind;
        public double stamp;
        public float deltaTime;
        public double relayOffset;
        // Shown stamp minus the tick the view clock says this kind presents.
        public double phase;
        public string detail;
    }

    public static readonly List<Sample> samples = new();
    public static ulong baseTick;

    public static void Record(PredictionManager pm, int kind, double stamp, string detail = null)
    {
        if (!pm || !pm.isClient || pm.cachedIsServer)
            return;

        samples.Add(new Sample
        {
            frame = Time.frameCount,
            kind = kind,
            stamp = stamp,
            deltaTime = Time.unscaledDeltaTime,
            relayOffset = pm.relayViewOffset,
            phase = stamp - ((kind == Control ? pm.viewTick : pm.viewTick - pm.relayViewOffset) - baseTick),
            detail = detail
        });
    }

    public static float FirstStamp(PredictionManager pm)
        => (float)((double)pm.localTickInContext + 1d - baseTick);
}

public struct RelayViewState : IPredictedData<RelayViewState>
{
    public float stamp;

    public void Dispose() { }
}

/// <summary>Server-driven identity predicted on every client: the smoothness reference.</summary>
public class RelayViewControlProbe : PredictedIdentity<RelayViewState>
{
    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.FullPrediction;

    protected override void Simulate(ref RelayViewState state, float delta)
        => state.stamp = state.stamp == 0f ? RelayViewLog.FirstStamp(predictionManager) : state.stamp + 1f;

    protected override RelayViewState Interpolate(RelayViewState from, RelayViewState to, float t)
        => new RelayViewState { stamp = Mathf.LerpUnclamped(from.stamp, to.stamp, t) };

    protected override void UpdateView(RelayViewState viewState, RelayViewState? verified)
        => RelayViewLog.Record(predictionManager, RelayViewLog.Control, viewState.stamp);
}

/// <summary>
/// Unowned PredictedIfOwned identity: resolves to ServerRelay on clients, like another player's
/// kart. Also hosts the relayed module probe.
/// </summary>
public class RelayViewStatefulProbe : PredictedIdentity<RelayViewState>
{
    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.PredictedIfOwned;

    protected override void LateAwake()
    {
        if (!TryGetModule<RelayViewModule>(out _))
            _ = new RelayViewModule(this);
    }

    protected override void Simulate(ref RelayViewState state, float delta)
        => state.stamp = state.stamp == 0f ? RelayViewLog.FirstStamp(predictionManager) : state.stamp + 1f;

    protected override RelayViewState Interpolate(RelayViewState from, RelayViewState to, float t)
        => new RelayViewState { stamp = Mathf.LerpUnclamped(from.stamp, to.stamp, t) };

    protected override void UpdateView(RelayViewState viewState, RelayViewState? verified)
    {
        var pm = predictionManager;
        if (!pm || !pm.isClient || pm.cachedIsServer)
            return;
        RelayViewLog.Record(pm, RelayViewLog.RelayStateful, viewState.stamp,
            $"policy={predictionPolicy} owner={owner} anchor={(long)viewAnchorTick - (long)RelayViewLog.baseTick} " +
            $"next={(long)viewNextSampleTick - (long)RelayViewLog.baseTick} pending={viewInterpolationBufferSize} " +
            $"view={pm.viewTick - RelayViewLog.baseTick:F2} offset={pm.relayViewOffset:F2} " +
            $"verified={(long)pm.verifiedServerTick - (long)RelayViewLog.baseTick} local={(long)pm.localTick - (long)RelayViewLog.baseTick} " +
            $"stamp={viewState.stamp:F2} verifiedStamp={(verified.HasValue ? verified.Value.stamp : -1f):F1}");
    }
}

public class RelayViewModule : PredictedModule<RelayViewState>
{
    public RelayViewModule(PredictedIdentity identity) : base(identity) { }

    protected override void Simulate(ref RelayViewState state, float delta)
        => state.stamp = state.stamp == 0f ? RelayViewLog.FirstStamp(identity.predictionManager) : state.stamp + 1f;

    protected override RelayViewState Interpolate(RelayViewState from, RelayViewState to, float t)
        => new RelayViewState { stamp = Mathf.LerpUnclamped(from.stamp, to.stamp, t) };

    protected override void UpdateView(RelayViewState viewState, RelayViewState? verifiedState)
        => RelayViewLog.Record(identity.predictionManager, RelayViewLog.RelayModule, viewState.stamp);
}

public class RelayViewDeterministicProbe : DeterministicIdentity<RelayViewDeterministicProbe.CountState>
{
    public struct CountState : IPredictedData<CountState>
    {
        public ulong count;
        // Written only by Interpolate, so the view can carry the fraction between two ticks.
        public float view;

        public void Dispose() { }
    }

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.PredictedIfOwned;

    protected override void Simulate(ref CountState state, sfloat delta)
    {
        ulong tick = predictionManager.localTickInContext + 1;
        state.count = state.count != 0 ? state.count + 1
            : tick > RelayViewLog.baseTick ? tick - RelayViewLog.baseTick : 1;
    }

    protected override CountState Interpolate(CountState from, CountState to, float t)
        => new CountState { count = to.count, view = (float)(from.count + ((double)to.count - from.count) * t) };

    protected override void UpdateView(CountState viewState, CountState? verified)
        => RelayViewLog.Record(predictionManager, RelayViewLog.RelayDeterministic, viewState.view);
}

/// <summary>
/// Objects relayed from the server (another player's object under PredictedIfOwned) move as
/// smoothly on a client as objects it predicts. Verified frames arrive unevenly, so a relayed view
/// sampled on the local tick a frame happened to land on moves 0, 1 or 2 ticks per tick.
/// Each client compares every relayed probe's per-frame speed with a predicted control probe.
/// </summary>
public class RelayViewSmoothnessScenario : Scenario
{
    private const int DigestChannel = 1720;
    private const float Timeout = 60f;
    private const float SettleSeconds = 1.5f;
    private const float SampleSeconds = 6f;
    // A frame whose speed differs from the control by more than this fraction of nominal speed
    // is a visible jump (3 m/s on a 20 m/s kart).
    private const double JumpSpeedFraction = 0.15d;
    // Extra share of jumping frames a relayed probe may show beyond the control. Under packet
    // loss a lost frame can arrive after the view reached its slot; the relayed view then waits
    // for it like any interpolation buffer, so it gets more room there. Unfixed relaying jumps
    // on over half the frames either way.
    private const double MaxExtraJumpShare = 0.02d;
    private const double MaxExtraJumpShareUnderLoss = 0.10d;
    private const int MinFrames = 120;
    private const double MaxPhaseTicks = 0.05d;

    private GameObject[] _prefabs;
    private int[] _prefabIds;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefabs = new[]
        {
            PredictionTestUtils.CreatePrefab<RelayViewControlProbe>("RelayViewControlProbe"),
            PredictionTestUtils.CreatePrefab<RelayViewStatefulProbe>("RelayViewStatefulProbe"),
            PredictionTestUtils.CreatePrefab<RelayViewDeterministicProbe>("RelayViewDeterministicProbe")
        };
        foreach (var prefab in _prefabs)
            PredictionTestUtils.RegisterPrefab(ctx, prefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        RelayViewLog.baseTick = startTick;
        RelayViewLog.samples.Clear();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        _prefabIds = new int[_prefabs.Length];
        for (var i = 0; i < _prefabs.Length; i++)
            pm.TryGetPrefab(_prefabs[i], out _prefabIds[i]);

        try
        {
            if (ctx.isServer)
            {
                for (var i = 0; i < _prefabs.Length; i++)
                {
                    if (!pm.hierarchy.Create(_prefabs[i], new Vector3(400f, 0f, i * 5f), Quaternion.identity).HasValue)
                        return ScenarioResult.Fail($"failed to create {_prefabs[i].name}");
                }
            }

            await UniTaskUtils.WaitWithTimeout(AllSpawned, Timeout, ctx.cancellationToken);
            await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);
            RelayViewLog.samples.Clear();
            await UniTask.WaitForSeconds(SampleSeconds, cancellationToken: ctx.cancellationToken);
        }
        catch (TimeoutException e)
        {
            return ScenarioResult.Fail(e.Message);
        }

        string failure = null;
        string report = ctx.isClient && !ctx.isServer
            ? Analyze(pm, ctx.hasPacketLoss ? MaxExtraJumpShareUnderLoss : MaxExtraJumpShare, out failure)
            : $"role={ctx.role} relays nothing";
        if (failure != null)
            return ScenarioResult.Fail($"{failure} | {report}");

        var digest = await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(pm), 30f);
        return digest.success ? ScenarioResult.Ok(report) : digest;

        bool AllSpawned()
        {
            for (var i = 0; i < _prefabIds.Length; i++)
            {
                if (PredictionTestUtils.CountInstances(pm, _prefabIds[i]) != 1)
                    return false;
            }
            return true;
        }
    }

    private static string Analyze(PredictionManager pm, double maxExtraJumpShare, out string failure)
    {
        failure = null;
        var byFrame = new SortedDictionary<int, (double[] stamps, bool[] has, float dt, string[] details)>();
        foreach (var s in RelayViewLog.samples)
        {
            if (!byFrame.TryGetValue(s.frame, out var row))
                row = (new double[RelayViewLog.KindCount], new bool[RelayViewLog.KindCount], s.deltaTime,
                    new string[RelayViewLog.KindCount]);
            row.stamps[s.kind] = s.stamp;
            row.details[s.kind] = s.detail;
            row.has[s.kind] = true;
            byFrame[s.frame] = row;
        }

        double tickRate = Math.Max(1, pm.tickRate);
        var frames = new int[RelayViewLog.KindCount];
        var jumps = new int[RelayViewLog.KindCount];
        var stalls = new int[RelayViewLog.KindCount];
        var speedErrors = new List<double>[RelayViewLog.KindCount];
        for (var k = 0; k < speedErrors.Length; k++)
            speedErrors[k] = new List<double>();

        string firstAnomaly = null;
        bool hasPrevious = false;
        (double[] stamps, bool[] has, float dt, string[] details) previous = default;
        int previousFrame = 0;
        foreach (var pair in byFrame)
        {
            var row = pair.Value;
            if (hasPrevious && pair.Key == previousFrame + 1 && row.dt > 0f &&
                row.has[RelayViewLog.Control] && previous.has[RelayViewLog.Control])
            {
                double nominal = row.dt * tickRate;
                double controlSpeed = (row.stamps[RelayViewLog.Control] - previous.stamps[RelayViewLog.Control]) / nominal;
                for (var k = 0; k < RelayViewLog.KindCount; k++)
                {
                    if (!row.has[k] || !previous.has[k])
                        continue;
                    double speed = (row.stamps[k] - previous.stamps[k]) / nominal;
                    double error = k == RelayViewLog.Control ? Math.Abs(speed - 1d) : Math.Abs(speed - controlSpeed);
                    frames[k]++;
                    speedErrors[k].Add(error);
                    if (error > JumpSpeedFraction)
                    {
                        jumps[k]++;
                        if (k == RelayViewLog.RelayStateful && firstAnomaly == null)
                        {
                            firstAnomaly = $"frame={pair.Key} speed={speed:F2} control={controlSpeed:F2} " +
                                           $"before[{previous.details[k]}] after[{row.details[k]}]";
                        }
                    }
                    if (speed <= 0d && controlSpeed > 0d)
                        stalls[k]++;
                }
            }

            previous = row;
            previousFrame = pair.Key;
            hasPrevious = true;
        }

        double controlShare = frames[RelayViewLog.Control] > 0
            ? jumps[RelayViewLog.Control] / (double)frames[RelayViewLog.Control]
            : 0d;

        double minOffset = double.MaxValue, maxOffset = 0d;
        int offsetRises = 0;
        double lastOffset = 0d;
        foreach (var s in RelayViewLog.samples)
        {
            if (s.kind != RelayViewLog.Control)
                continue;
            minOffset = Math.Min(minOffset, s.relayOffset);
            maxOffset = Math.Max(maxOffset, s.relayOffset);
            if (lastOffset > 0d && s.relayOffset > lastOffset + 0.5d)
                offsetRises++;
            lastOffset = s.relayOffset;
        }

        var phaseSum = new double[RelayViewLog.KindCount];
        var phaseCount = new int[RelayViewLog.KindCount];
        var phaseMin = new double[RelayViewLog.KindCount];
        var phaseMax = new double[RelayViewLog.KindCount];
        for (var k = 0; k < RelayViewLog.KindCount; k++)
        {
            phaseMin[k] = double.MaxValue;
            phaseMax[k] = double.MinValue;
        }
        foreach (var s in RelayViewLog.samples)
        {
            phaseSum[s.kind] += s.phase;
            phaseCount[s.kind]++;
            phaseMin[s.kind] = Math.Min(phaseMin[s.kind], s.phase);
            phaseMax[s.kind] = Math.Max(phaseMax[s.kind], s.phase);
        }

        var report = new StringBuilder();
        report.Append("phase{");
        for (var k = 0; k < RelayViewLog.KindCount; k++)
        {
            if (phaseCount[k] > 0)
                report.Append($"{RelayViewLog.names[k]}={phaseSum[k] / phaseCount[k]:F3}[{phaseMin[k]:F2},{phaseMax[k]:F2}] ");
        }
        report.Append("} ");
        // A relayed view must show the state of the tick the view clock presents for it, on the
        // same convention as predicted samples; lag compensation rewinds relayed colliders there.
        // The deterministic probe counts from its own first tick, so only its steadiness counts.
        foreach (var k in new[] { RelayViewLog.Control, RelayViewLog.RelayStateful, RelayViewLog.RelayModule })
        {
            if (failure == null && phaseCount[k] > 0 && (Math.Abs(phaseMin[k]) > MaxPhaseTicks || Math.Abs(phaseMax[k]) > MaxPhaseTicks))
                failure = $"{RelayViewLog.names[k]} showed states {phaseMin[k]:F2}..{phaseMax[k]:F2} ticks away from the tick the view clock presents";
        }
        report.Append($"tickRate={pm.tickRate} lead={pm.lead} relayOffset={minOffset:F2}..{maxOffset:F2} rises={offsetRises}" +
                      $" viewStarved={pm.viewBufferStarvedFramesTotal} trims={pm.viewBufferTrimsTotal}");
        for (var k = 0; k < RelayViewLog.KindCount; k++)
        {
            double share = frames[k] > 0 ? jumps[k] / (double)frames[k] : 0d;
            report.Append($" | {RelayViewLog.names[k]} frames={frames[k]} jumps={jumps[k]} ({share:P1}) stalls={stalls[k]}" +
                          $" p95={Percentile(speedErrors[k], 0.95d):F2} max={Percentile(speedErrors[k], 1d):F2}");

            if (failure != null)
                continue;
            if (frames[k] < MinFrames)
                failure = $"{RelayViewLog.names[k]} produced only {frames[k]} comparable frames, expected at least {MinFrames}";
            else if (k != RelayViewLog.Control && share > controlShare + maxExtraJumpShare)
                failure = $"{RelayViewLog.names[k]} jumped on {share:P1} of frames versus {controlShare:P1} for the predicted control";
        }

        if (firstAnomaly != null)
            report.Append(" | firstStatefulJump ").Append(firstAnomaly);
        return report.ToString();
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0d;
        var sorted = new List<double>(values);
        sorted.Sort();
        int index = (int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1);
        return sorted[Math.Max(0, index)];
    }

    private string BuildDigest(PredictionManager pm)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _prefabIds.Length; i++)
            sb.Append(PredictionTestUtils.CountInstances(pm, _prefabIds[i])).Append(';');
        return sb.ToString();
    }
}
