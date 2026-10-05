using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class TrailIntegrityScenario : Scenario
{
    [SerializeField] private float _fireSeconds = 15f;
    [SerializeField] private float _timeout = 120f;

    private const int DigestChannel = 1300;
    private const double InputSlackLowerBoundMs = 2d;
    private const double InputSlackUpperSlopMs = 30d;
    private const double MaxStandingSlackTicks = 2.3d;
    private const float InputSlackWarmupSeconds = 5f;
    private const long MinSmoothnessFrames = 200;
    private const double MaxUnconfirmedExtraStepPercent = 10d;

    private GameObject _gunnerPrefab;
    private GameObject _projectilePrefab;
    private int _gunnerPrefabId;
    private int _projectilePrefabId;
    private ulong _fireEndTick;
    private ulong _slackSampleStartTick;
    private double _slackSampleSum;
    private double _slackTargetSum;
    private long _slackSampleCount;
    private double _slackSampleMin;
    private double _slackSampleMax;
    private ulong _hitchBaseline;
    private bool _hasHitchBaseline;
    private double _maxFrameGapMs;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _projectilePrefab = new GameObject("TrailProjectile");
        _projectilePrefab.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_projectilePrefab);

        var pt = _projectilePrefab.AddComponent<PredictedTransform>();
        _projectilePrefab.AddComponent<TrailProjectile>();
        _projectilePrefab.AddComponent<TrailViewTracker>();

        var view = new GameObject("view");
        view.transform.SetParent(_projectilePrefab.transform, false);

        var settings = ScriptableObject.CreateInstance<TransformInterpolationSettings>();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PredictedTransform).GetField("_interpolationSettings", flags)?.SetValue(pt, settings);
        typeof(PredictedTransform).GetField("_graphics", flags)?.SetValue(pt, view.transform);

        PredictionTestUtils.RegisterPrefab(ctx, _projectilePrefab, true, 4);

        _gunnerPrefab = PredictionTestUtils.CreatePrefab<TrailGunner>("TrailGunner");
        PredictionTestUtils.RegisterPrefab(ctx, _gunnerPrefab);

        TrailGunner.projectilePrefab = _projectilePrefab;
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        var tickRate = ctx.predictionManager.tickRate;
        TrailGunner.fireStartTick = startTick;
        _fireEndTick = startTick + (ulong)Mathf.CeilToInt(_fireSeconds * tickRate);
        TrailGunner.fireEndTick = _fireEndTick;
        _slackSampleStartTick = startTick + (ulong)Mathf.CeilToInt(InputSlackWarmupSeconds * tickRate);
        _slackSampleSum = 0;
        _slackTargetSum = 0;
        _slackSampleCount = 0;
        _slackSampleMin = double.MaxValue;
        _slackSampleMax = double.MinValue;
        _hitchBaseline = 0;
        _hasHitchBaseline = false;
        _maxFrameGapMs = 0;
        TrailViewTracker.ResetAll();
    }

    private static ulong CountHitches(PredictionManager pm)
    {
        return pm.leadJumpsTotal + pm.leadPausesTotal + pm.minLeadSnapsTotal + pm.starvationJumpsTotal;
    }

    private void SampleInputSlack(PredictionManager pm)
    {
        if (!pm.hasInputSlackFeedback || pm.time.tick < _slackSampleStartTick)
            return;

        if (!_hasHitchBaseline)
        {
            _hitchBaseline = CountHitches(pm);
            _hasHitchBaseline = true;
        }

        // A long render frame means this process stalled (GC, scheduler); keep it in the report so a
        // hitch can be told apart from a controller fault.
        double frameGapMs = Time.unscaledDeltaTime * 1000d;
        if (frameGapMs > _maxFrameGapMs)
            _maxFrameGapMs = frameGapMs;

        var slack = pm.lastInputSlackMs;
        _slackSampleSum += slack;
        _slackTargetSum += pm.currentSlackTargetMs;
        _slackSampleCount++;
        if (slack < _slackSampleMin) _slackSampleMin = slack;
        if (slack > _slackSampleMax) _slackSampleMax = slack;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_gunnerPrefab, out _gunnerPrefabId);
        pm.TryGetPrefab(_projectilePrefab, out _projectilePrefabId);

        int expectedGunners = ctx.expectedConnections;

        if (ctx.isServer)
        {
            var owners = new List<PlayerID>(pm.players.players);
            owners.Sort((a, b) => a.id.value.CompareTo(b.id.value));

            if (owners.Count != expectedGunners)
                return ScenarioResult.Fail($"expected {expectedGunners} players, saw {owners.Count}");

            for (var i = 0; i < owners.Count; i++)
            {
                var position = new Vector3(0f, 0f, i * 50f);
                if (!pm.hierarchy.Create(_gunnerPrefab, position, Quaternion.identity, owners[i]).HasValue)
                    return ScenarioResult.Fail($"failed to create gunner {i}");
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, _gunnerPrefabId) == expectedGunners && HasOwnedGunner(ctx),
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"gunners never appeared: count={PredictionTestUtils.CountInstances(pm, _gunnerPrefabId)} " +
                $"owned={HasOwnedGunner(ctx)}");
        }

        var quietTick = _fireEndTick + TrailProjectile.LifetimeTicks + (ulong)pm.tickRate * 2;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () =>
                {
                    SampleInputSlack(pm);
                    return pm.time.tick >= quietTick && PredictionTestUtils.CountInstances(pm, _projectilePrefabId) == 0;
                },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"projectiles never drained: tick={pm.time.tick}/{quietTick} " +
                $"live={PredictionTestUtils.CountInstances(pm, _projectilePrefabId)}");
        }

        await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);

        var digest = BuildDigest(ctx);
        var digestResult = await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);

        var report = BuildTrackerReport();

        bool pureClient = ctx.isClient && !ctx.isServer;
        double avgSlack = _slackSampleCount > 0 ? _slackSampleSum / _slackSampleCount : 0;
        double avgTarget = _slackSampleCount > 0 ? _slackTargetSum / _slackSampleCount : 0;

        if (pureClient)
        {
            report += $" | applies render={pm.renderPhaseFrameAppliesTotal} tick={pm.tickPhaseFrameAppliesTotal} maxAge={pm.maxFrameApplyAgeFrames}";
            report += $" | viewBuffer trims={pm.viewBufferTrimsTotal} starved={pm.viewBufferStarvedFramesTotal}";
            report += $" | inputSlack avg={avgSlack:F1}ms min={_slackSampleMin:F1} max={_slackSampleMax:F1} target={avgTarget:F1}ms ema={pm.smoothedInputSlackMs:F1}ms scale={pm.currentTickPacingScale:F4} samples={_slackSampleCount}";
            report += $" | lead jumps={pm.leadJumpsTotal} pauses={pm.leadPausesTotal} snaps={pm.minLeadSnapsTotal} starv={pm.starvationJumpsTotal} windowHitches={(_hasHitchBaseline ? CountHitches(pm) - _hitchBaseline : 0)}";
            report += $" | maxFrameGap={_maxFrameGapMs:F1}ms | net={ctx.minLatencyMs}-{ctx.maxLatencyMs}ms loss={ctx.packetLossPercent}%";
        }

        Debug.Log($"[TrailIntegrity] {ctx.role} {report}");

        if (TrailViewTracker.failures.Count > 0)
            return ScenarioResult.Fail(report);

        // An unconfirmed projectile is re-created by every reconcile and can change id when other
        // players' spawns land first; its view must still glide like a confirmed one. Comparing the two
        // keeps network jitter, which affects both, out of the verdict.
        var unconfirmed = TrailViewTracker.unconfirmedSmoothness;
        var confirmed = TrailViewTracker.confirmedSmoothness;
        if (unconfirmed.frames >= MinSmoothnessFrames &&
            unconfirmed.stepPercent > confirmed.stepPercent + MaxUnconfirmedExtraStepPercent)
            return ScenarioResult.Fail($"unconfirmed projectiles stepped instead of gliding: {report}");

        if (pureClient)
        {
            if (pm.renderPhaseFrameAppliesTotal + pm.tickPhaseFrameAppliesTotal == 0)
                return ScenarioResult.Fail($"no server frames were applied: {report}");

            // Frames received in NetworkManager.Update must reconcile in the same render frame. Queue age
            // is stamped when a frame becomes eligible (staged continuations wait for their checkpoint by
            // design), so any age above one frame is a scheduling defect. Only the performance cadence
            // (-reconcileMs) legitimately holds frames back.
            if (PredictionPerformanceTelemetry.reconcileIntervalSeconds <= 0 && pm.maxFrameApplyAgeFrames > 1)
                return ScenarioResult.Fail($"server frames waited {pm.maxFrameApplyAgeFrames} render frames before applying: {report}");

            if (!pm.hasInputSlackFeedback || _slackSampleCount == 0)
                return ScenarioResult.Fail($"no input slack feedback received: {report}");

            double structuralFloorMs = MaxStandingSlackTicks * pm.tickDelta * 1000d;
            double upperBoundMs = System.Math.Max(avgTarget + InputSlackUpperSlopMs, structuralFloorMs);

            if (avgSlack < InputSlackLowerBoundMs || avgSlack > upperBoundMs)
                return ScenarioResult.Fail($"input slack out of band [{InputSlackLowerBoundMs:F0}ms, {upperBoundMs:F0}ms]: {report}");

            ulong windowHitches = _hasHitchBaseline ? CountHitches(pm) - _hitchBaseline : 0;
            if (windowHitches > 0)
            {
                // Without loss the lead controller guarantees a steady head. Under loss it re-targets the
                // lead when a burst of input packets is lost, which is its job, not a fault; report those
                // adjustments instead of failing on them.
                if (!ctx.hasPacketLoss)
                    return ScenarioResult.Fail($"prediction head hitched during the steady-state window: {report}");

                report = $"NOTE: {windowHitches} lead adjustment(s) during the steady-state window under {ctx.packetLossPercent}% packet loss | {report}";
            }
        }

        if (!digestResult.success)
            return digestResult;

        return ScenarioResult.Ok(report);
    }

    private static bool HasOwnedGunner(ScenarioContext ctx)
    {
        if (!ctx.isClient)
            return true;

        var pm = ctx.predictionManager;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.instanceId.TryGetComponent<TrailGunner>(pm, out var gunner) && gunner.isOwner)
                return true;
        }

        return false;
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();
        sb.Append($"gunners={PredictionTestUtils.CountInstances(pm, _gunnerPrefabId)};");
        sb.Append($"projectiles={PredictionTestUtils.CountInstances(pm, _projectilePrefabId)};");
        PredictionTestUtils.AppendIdentities<TrailGunner>(pm, _gunnerPrefabId, sb, gunner => gunner.Digest());
        return sb.ToString();
    }

    private static string BuildTrackerReport()
    {
        var sb = new StringBuilder();
        sb.Append($"samples={TrailViewTracker.totalSamples} segments={TrailViewTracker.segmentsStarted} ");
        sb.Append($"failures={TrailViewTracker.failures.Count} diagnostics={TrailViewTracker.diagnostics.Count} ");
        sb.Append($"resurrections={TrailViewTracker.resurrections} maxBackward={TrailViewTracker.maxBackward:F3} ");
        sb.Append($"kinds={TrailViewTracker.DescribeKindCounts()} ");
        sb.Append($"smooth[unconfirmed {TrailViewTracker.unconfirmedSmoothness} | confirmed {TrailViewTracker.confirmedSmoothness}]");

        AppendSamples(sb, " | FAIL ", TrailViewTracker.failures, 10);
        AppendSamples(sb, " | diag ", TrailViewTracker.diagnostics, 10);
        return sb.ToString();
    }

    private static void AppendSamples(StringBuilder sb, string prefix, List<TrailViewTracker.Sample> samples, int max)
    {
        var count = Mathf.Min(samples.Count, max);
        for (var i = 0; i < count; i++)
            sb.Append(prefix).Append(samples[i]);
    }
}
