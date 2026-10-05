using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Server-controlled probe whose state is the tick it was simulated for. Interpolating that
/// state gives the exact prediction tick the view is presenting, so probes spawned at different
/// times expose any phase difference between their view buffers and the manager's view clock.
/// </summary>
public class ViewPhaseProbe : PredictedIdentity<ViewPhaseProbe.PhaseState>
{
    public static ulong baseTick;

    public struct PhaseState : IPredictedData<PhaseState>
    {
        public float stamp;

        public void Dispose() { }
    }

    public struct Sample
    {
        public int frame;
        public int instance;
        public double stamp;
        public double viewTick;
        public double localTick;
        public int bufferAhead;
        public double anchorTick;
        public double nextTick;
        public bool hasNext;

        public bool gliding => hasNext && nextTick - anchorTick > 1.5d;

        public override string ToString()
            => $"frame={frame} instance={instance} stamp={stamp:F4} clock={viewTick:F4} local={localTick:F1} " +
               $"ahead={bufferAhead} anchor={anchorTick:F0} next={nextTick:F0}";
    }

    public static readonly List<Sample> samples = new();

    public static void ResetAll() => samples.Clear();

    protected override void Simulate(ref PhaseState state, float delta)
    {
        if (state.stamp == 0f)
            state.stamp = (float)((double)predictionManager.localTickInContext + 1d - baseTick);
        else
            state.stamp += 1f;
    }

    protected override void UpdateView(PhaseState viewState, PhaseState? verified)
    {
        var pm = predictionManager;
        if (!pm || !pm.isClient)
            return;

        samples.Add(new Sample
        {
            frame = Time.frameCount,
            instance = (int)id.objectId.instanceId.value,
            stamp = viewState.stamp,
            viewTick = pm.viewTick - baseTick,
            localTick = (double)pm.localTick - baseTick,
            bufferAhead = viewInterpolationBufferSize,
            anchorTick = (double)viewAnchorTick - baseTick,
            nextTick = (double)viewNextSampleTick - baseTick,
            hasNext = viewNextSampleTick != 0
        });
    }
}

/// <summary>
/// Every identity on screen presents the same prediction tick, regardless of when it spawned, and
/// that tick is the manager's view clock. Three probes are spawned at staggered wall-clock times;
/// after each settles, per-frame spreads between probes and deviations from the clock must stay
/// within a small fraction of a tick.
/// </summary>
public class ViewPhaseScenario : Scenario
{
    private const int DigestChannel = 1710;
    private const float Timeout = 60f;
    private const float SampleSeconds = 4f;
    private const double SettleTicks = 3d;
    private const double MaxSpreadTicks = 0.02d;
    private const double MaxClockDeviationTicks = 0.02d;
    private const int MinFrames = 60;
    private const int ProbeCount = 3;

    private static readonly float[] SpawnDelays = { 0f, 0.73f, 0.61f };

    private GameObject _probePrefab;
    private int _probePrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = PredictionTestUtils.CreatePrefab<ViewPhaseProbe>("ViewPhaseProbe");
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        ViewPhaseProbe.baseTick = startTick;
        ViewPhaseProbe.ResetAll();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_probePrefab, out _probePrefabId);

        try
        {
            if (ctx.isServer)
            {
                for (var i = 0; i < ProbeCount; i++)
                {
                    if (SpawnDelays[i] > 0f)
                        await UniTask.WaitForSeconds(SpawnDelays[i], cancellationToken: ctx.cancellationToken);

                    var position = new Vector3(300f, 0f, i * 5f);
                    if (!pm.hierarchy.Create(_probePrefab, position, Quaternion.identity).HasValue)
                        return ScenarioResult.Fail($"failed to create probe {i}");
                }
            }

            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, _probePrefabId) == ProbeCount,
                Timeout,
                ctx.cancellationToken);

            await UniTask.WaitForSeconds(SampleSeconds, cancellationToken: ctx.cancellationToken);
        }
        catch (TimeoutException e)
        {
            return ScenarioResult.Fail(e.Message);
        }

        string failure = null;
        var report = ctx.isClient ? Analyze(pm, out failure) : $"role={ctx.role} no local view";
        if (failure != null)
            return ScenarioResult.Fail($"{failure} | {report}");

        var digest = await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(pm), 30f);
        return digest.success ? ScenarioResult.Ok(report) : digest;
    }

    private static string Analyze(PredictionManager pm, out string failure)
    {
        failure = null;
        var samples = ViewPhaseProbe.samples;

        var settleAfter = new Dictionary<int, double>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!settleAfter.ContainsKey(s.instance))
                settleAfter[s.instance] = s.viewTick + SettleTicks;
        }

        int settledSamples = 0, gliding = 0, holding = 0;
        int glidingIndex = -1;
        var offsets = new List<double>();
        var offsetIndices = new List<int>();

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.viewTick < settleAfter[s.instance])
                continue;

            settledSamples++;
            if (s.gliding)
            {
                gliding++;
                if (glidingIndex < 0) glidingIndex = i;
                continue;
            }

            if (s.bufferAhead <= 0)
            {
                holding++;
                continue;
            }

            offsets.Add(s.stamp - s.viewTick);
            offsetIndices.Add(i);
        }

        double typicalOffset = 0d;
        if (offsets.Count > 0)
        {
            var sorted = new List<double>(offsets);
            sorted.Sort();
            typicalOffset = sorted[sorted.Count / 2];
        }

        int anomalies = 0;
        int firstAnomalyIndex = -1;
        double maxDeviation = 0d;
        int worstIndex = -1;
        var anomalous = new HashSet<int>();
        for (var k = 0; k < offsets.Count; k++)
        {
            double deviation = Math.Abs(offsets[k] - typicalOffset);
            if (deviation > MaxClockDeviationTicks)
            {
                anomalies++;
                anomalous.Add(offsetIndices[k]);
                if (firstAnomalyIndex < 0) firstAnomalyIndex = offsetIndices[k];
                continue;
            }

            if (deviation > maxDeviation)
            {
                maxDeviation = deviation;
                worstIndex = offsetIndices[k];
            }
        }

        var frameStamps = new Dictionary<int, List<double>>();
        var frameExcluded = new HashSet<int>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.viewTick < settleAfter[s.instance])
                continue;
            if (s.gliding || s.bufferAhead <= 0 || anomalous.Contains(i))
            {
                frameExcluded.Add(s.frame);
                continue;
            }

            if (!frameStamps.TryGetValue(s.frame, out var list))
                frameStamps[s.frame] = list = new List<double>(ProbeCount);
            list.Add(s.stamp);
        }

        int fullFrames = 0;
        double maxSpread = 0d;
        string worstSpread = null;
        foreach (var pair in frameStamps)
        {
            if (pair.Value.Count < ProbeCount || frameExcluded.Contains(pair.Key))
                continue;

            fullFrames++;
            double min = double.MaxValue, max = double.MinValue;
            for (var i = 0; i < pair.Value.Count; i++)
            {
                min = Math.Min(min, pair.Value[i]);
                max = Math.Max(max, pair.Value[i]);
            }

            double spread = max - min;
            if (spread > maxSpread)
            {
                maxSpread = spread;
                worstSpread = $"frame={pair.Key} min={min:F4} max={max:F4}";
            }
        }

        var report = new StringBuilder();
        report.Append($"probes={settleAfter.Count} samples={samples.Count} settled={settledSamples} fullFrames={fullFrames}");
        report.Append($" maxSpreadTicks={maxSpread:F4} maxClockDeviationTicks={maxDeviation:F4} clockOffset={typicalOffset:F4}");
        report.Append($" interpolating={offsets.Count} anomalies={anomalies} holding={holding} gliding={gliding}");
        report.Append($" viewBuffer trims={pm.viewBufferTrimsTotal} starved={pm.viewBufferStarvedFramesTotal}");
        if (worstSpread != null) report.Append($" | worstSpread {worstSpread}");
        if (worstIndex >= 0) report.Append($" | worstDeviation {samples[worstIndex]}");
        if (firstAnomalyIndex >= 0)
        {
            report.Append($" | firstAnomaly {samples[firstAnomalyIndex]}");
            for (int i = firstAnomalyIndex - 1, shown = 0; i >= 0 && shown < 1; i--)
            {
                if (samples[i].instance != samples[firstAnomalyIndex].instance) continue;
                report.Append($" | beforeAnomaly {samples[i]}");
                shown++;
            }
        }
        if (glidingIndex >= 0) report.Append($" | firstGliding {samples[glidingIndex]}");

        if (settleAfter.Count < ProbeCount)
            failure = $"only {settleAfter.Count} probes produced view samples";
        else if (fullFrames < MinFrames)
            failure = $"only {fullFrames} frames had all probes interpolating normally, expected at least {MinFrames}";
        else if (maxSpread > MaxSpreadTicks)
            failure = $"probes spawned at different times drifted {maxSpread:F4} ticks apart in the same frame";
        else if (anomalies > offsets.Count / 100)
            failure = $"{anomalies} of {offsets.Count} interpolating samples were off the manager view clock";

        return report.ToString();
    }

    private string BuildDigest(PredictionManager pm)
    {
        var sb = new StringBuilder();
        PredictionTestUtils.AppendIdentities<ViewPhaseProbe>(pm, _probePrefabId, sb,
            probe => probe.id.objectId.instanceId.value.ToString());
        return sb.ToString();
    }
}
